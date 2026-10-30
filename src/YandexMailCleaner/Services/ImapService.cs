using System.Diagnostics;
using System.IO;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using YandexMailCleaner.Models;

namespace YandexMailCleaner.Services;

/// <summary>
/// Работа с Яндекс.Почтой по IMAP: подключение, список папок,
/// поиск писем по фильтрам и перемещение найденного в Корзину.
///
/// Яндекс закрывает простаивающее IMAP-соединение примерно через 90 секунд,
/// поэтому после подключения работает «пульс» NOOP каждые 25 секунд,
/// а перед каждой операцией выполняется автопереподключение при необходимости.
/// </summary>
public sealed class ImapService : IAsyncDisposable
{
    public const string Host = "imap.yandex.ru";
    public const int Port = 993;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FolderTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan SizeFetchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MoveBatchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(25);

    private const int MoveBatchSize = 200;
    private const int SizeFetchBatchSize = 2000;

    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private ImapClient? _client;
    private string _password = "";
    private CancellationTokenSource? _keepAliveCts;
    private Task? _keepAliveTask;

    public string Email { get; private set; } = "";

    public bool IsConnected => _client is { IsConnected: true, IsAuthenticated: true };

    private ImapClient Client =>
        _client ?? throw new InvalidOperationException("Нет подключения к серверу.");

    public async Task ConnectAsync(string email, string appPassword, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            var client = new ImapClient { Timeout = 120_000 };
            try
            {
                await WithTimeoutAsync(
                    t => client.ConnectAsync(Host, Port, SecureSocketOptions.SslOnConnect, t),
                    ConnectTimeout, ct).ConfigureAwait(false);

                await WithTimeoutAsync(
                    t => client.AuthenticateAsync(email, appPassword, t),
                    ConnectTimeout, ct).ConfigureAwait(false);
            }
            catch
            {
                try { client.Dispose(); } catch { }
                throw;
            }

            _client = client;
            Email = email;
            _password = appPassword;
            StartKeepAlive(client);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Переподключается, если соединение было потеряно (например, из-за простоя).</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            return;

        if (string.IsNullOrEmpty(Email) || string.IsNullOrEmpty(_password))
            throw new InvalidOperationException(
                "Нет подключения к серверу. Войдите в аккаунт заново.");

        AppLogger.Info("Соединение потеряно, выполняем переподключение…");
        await ConnectAsync(Email, _password, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task DisconnectCoreAsync()
    {
        var keepAliveCts = _keepAliveCts;
        _keepAliveCts = null;
        keepAliveCts?.Cancel();

        var keepAliveTask = _keepAliveTask;
        _keepAliveTask = null;
        if (keepAliveTask is not null)
        {
            try { await keepAliveTask.ConfigureAwait(false); } catch { }
        }
        keepAliveCts?.Dispose();

        var client = _client;
        _client = null;
        if (client is null)
            return;

        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch
        {
            // игнорируем ошибки отключения
        }
        finally
        {
            client.Dispose();
        }
    }

    private void StartKeepAlive(ImapClient client)
    {
        var cts = new CancellationTokenSource();
        _keepAliveCts = cts;

        _keepAliveTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(KeepAliveInterval, cts.Token).ConfigureAwait(false);

                    if (!client.IsConnected)
                        return;

                    await client.NoOpAsync(cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // штатное завершение
            }
            catch (Exception ex)
            {
                AppLogger.Error("IMAP keep-alive остановлен (соединение потеряно)", ex);
            }
        });
    }

    public async Task<List<MailFolderInfo>> GetFoldersAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var client = Client;
        var personal = client.PersonalNamespaces[0];
        var folders = await WithTimeoutAsync(
            t => client.GetFoldersAsync(personal, subscribedOnly: false, t),
            FolderTimeout, ct).ConfigureAwait(false);

        var result = new List<MailFolderInfo>();
        var inboxSeen = false;

        foreach (var folder in folders)
        {
            if (folder.Attributes.HasFlag(FolderAttributes.NoSelect) ||
                folder.Attributes.HasFlag(FolderAttributes.NonExistent))
                continue;

            var kind = KindFrom(folder);
            if (kind == MailFolderKind.Inbox)
                inboxSeen = true;

            result.Add(new MailFolderInfo
            {
                Path = folder.FullName,
                DisplayName = DisplayName(folder, kind),
                Kind = kind
            });
        }

        if (!inboxSeen)
        {
            result.Insert(0, new MailFolderInfo
            {
                Path = "INBOX",
                DisplayName = "Входящие",
                Kind = MailFolderKind.Inbox
            });
        }

        return result
            .OrderByDescending(f => f.Kind == MailFolderKind.Inbox)
            .ThenBy(f => f.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// Ищет письма по фильтрам. Тела и размеры писем не скачиваются:
    /// фильтрация выполняется на сервере, результат — список UID.
    /// </summary>
    public async Task<CleanupPlan> BuildPlanAsync(
        CleanupOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var query = BuildQuery(options);
        var plans = new List<FolderPlan>();

        foreach (var path in options.FolderPaths)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await SearchFolderWithRetryAsync(path, query, progress, ct).ConfigureAwait(false);
            if (outcome is null)
                continue;

            if (outcome.Uids.Count == 0)
            {
                progress?.Report($"«{outcome.DisplayName}»: подходящих писем нет.");
                continue;
            }

            plans.Add(new FolderPlan
            {
                Path = outcome.Path,
                DisplayName = outcome.DisplayName,
                Uids = outcome.Uids,
                Bytes = outcome.Bytes
            });

            progress?.Report($"«{outcome.DisplayName}»: найдено {outcome.Uids.Count} писем.");
        }

        return new CleanupPlan { Folders = plans };
    }

    private async Task<FolderSearchOutcome?> SearchFolderWithRetryAsync(
        string path,
        SearchQuery query,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);

                var client = Client;
                var folder = await WithTimeoutAsync(
                    t => client.GetFolderAsync(path, t), FolderTimeout, ct).ConfigureAwait(false);

                var fullName = folder.FullName;
                var display = folder.Name;

                progress?.Report($"Поиск в папке «{display}»…");
                AppLogger.Info($"Поиск: папка '{path}', запрос: {query}");

                await WithTimeoutAsync(
                    t => folder.OpenAsync(FolderAccess.ReadOnly, t),
                    FolderTimeout, ct).ConfigureAwait(false);

                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var uids = await WithTimeoutAsync(
                        t => folder.SearchAsync(query, t),
                        SearchTimeout, ct).ConfigureAwait(false);
                    stopwatch.Stop();

                    AppLogger.Info(
                        $"Поиск: папка '{path}', найдено {uids.Count}, {stopwatch.Elapsed.TotalSeconds:0.0} c");

                    long totalBytes = 0;
                    if (uids.Count > 0)
                    {
                        progress?.Report($"«{display}»: подсчёт общего размера…");
                        totalBytes = await GetTotalSizeAsync(folder, uids, ct).ConfigureAwait(false);
                    }

                    return new FolderSearchOutcome
                    {
                        Path = fullName,
                        DisplayName = display,
                        Uids = uids,
                        Bytes = totalBytes
                    };
                }
                finally
                {
                    try { await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
                }
            }
            catch (Exception ex) when (attempt < maxAttempts && IsConnectionError(ex))
            {
                AppLogger.Error($"Соединение потеряно при поиске в '{path}', переподключаемся", ex);
                await DisconnectAsync().ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Суммарный размер писем в байтах. Тела писем не скачиваются:
    /// сервер возвращает только RFC822.SIZE (атрибут SIZE), запрос идёт пакетами.
    /// </summary>
    private static async Task<long> GetTotalSizeAsync(
        IMailFolder folder,
        IList<UniqueId> uids,
        CancellationToken ct)
    {
        long total = 0;

        try
        {
            for (var offset = 0; offset < uids.Count; offset += SizeFetchBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = uids.Skip(offset).Take(SizeFetchBatchSize).ToList();
                var summaries = await WithTimeoutAsync(
                    t => folder.FetchAsync(batch, MessageSummaryItems.Size, t),
                    SizeFetchTimeout, ct).ConfigureAwait(false);

                foreach (var summary in summaries)
                {
                    if (summary.Size is { } size)
                        total += size;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Не удалось подсчитать общий размер писем", ex);
            return 0;
        }

        return total;
    }

    /// <summary>Перемещает найденные письма в Корзину Яндекса пакетами.</summary>
    public async Task<CleanupResult> ExecutePlanAsync(
        CleanupPlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        var totalToMove = plan.Folders.Sum(f => f.Uids.Count);
        var moved = 0;
        long movedBytes = 0;
        var failedFolders = 0;
        var processed = 0;

        foreach (var folderPlan in plan.Folders)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var client = Client;
            var trash = await GetTrashFolderAsync(client, ct).ConfigureAwait(false);
            var trashName = trash.FullName;

            if (string.Equals(folderPlan.Path, trashName, StringComparison.OrdinalIgnoreCase))
                continue;

            progress?.Report(
                $"Перемещение {processed}/{plan.Folders.Count}: «{folderPlan.DisplayName}»…");

            try
            {
                var folder = await WithTimeoutAsync(
                    t => client.GetFolderAsync(folderPlan.Path, t), FolderTimeout, ct).ConfigureAwait(false);

                await WithTimeoutAsync(
                    t => folder.OpenAsync(FolderAccess.ReadWrite, t),
                    FolderTimeout, ct).ConfigureAwait(false);

                try
                {
                    for (var offset = 0; offset < folderPlan.Uids.Count; offset += MoveBatchSize)
                    {
                        ct.ThrowIfCancellationRequested();

                        var batch = folderPlan.Uids
                            .Skip(offset)
                            .Take(MoveBatchSize)
                            .ToList();

                        await WithTimeoutAsync(
                            t => folder.MoveToAsync(batch, trash, t),
                            MoveBatchTimeout, ct).ConfigureAwait(false);

                        moved += batch.Count;
                        progress?.Report($"Перемещение: {moved} из {totalToMove}…");
                    }

                    movedBytes += folderPlan.Bytes;

                    AppLogger.Info(
                        $"В Корзину: папка '{folderPlan.Path}', писем {folderPlan.Uids.Count}");
                }
                finally
                {
                    try { await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                failedFolders++;
                AppLogger.Error($"Ошибка перемещения в '{folderPlan.Path}'", ex);
            }
        }

        return new CleanupResult
        {
            MovedCount = moved,
            MovedBytes = movedBytes,
            FailedFolders = failedFolders
        };
    }

    private static SearchQuery BuildQuery(CleanupOptions options)
    {
        SearchQuery? query = null;

        void Add(SearchQuery part) => query = query is null ? part : query.And(part);

        switch (options.DateMode)
        {
            case DateFilterMode.OlderThan:
            {
                var months = options.OlderThanMonths <= 0 ? 6 : options.OlderThanMonths;
                Add(SearchQuery.DeliveredBefore(DateTime.Today.AddMonths(-months)));
                break;
            }
            case DateFilterMode.Range:
            {
                if (options.RangeFrom.HasValue)
                    Add(SearchQuery.DeliveredAfter(options.RangeFrom.Value.Date));
                if (options.RangeTo.HasValue)
                    Add(SearchQuery.DeliveredBefore(options.RangeTo.Value.Date.AddDays(1)));
                break;
            }
        }

        if (options.SizeMode == SizeFilterMode.LargerThan && options.MinSizeBytes > 0)
            Add(SearchQuery.LargerThan((int)Math.Min(options.MinSizeBytes, int.MaxValue)));

        return query ?? SearchQuery.All;
    }

    private static async Task<IMailFolder> GetTrashFolderAsync(ImapClient client, CancellationToken ct)
    {
        var personal = client.PersonalNamespaces[0];
        var folders = await WithTimeoutAsync(
            t => client.GetFoldersAsync(personal, subscribedOnly: false, t),
            FolderTimeout, ct).ConfigureAwait(false);

        foreach (var folder in folders)
        {
            if (KindFrom(folder) == MailFolderKind.Trash)
                return folder;
        }

        throw new InvalidOperationException(
            "Не удалось найти папку «Корзина» на сервере Яндекса.");
    }

    private static MailFolderKind KindFrom(IMailFolder folder)
    {
        var attributes = folder.Attributes;

        if (attributes.HasFlag(FolderAttributes.Inbox)) return MailFolderKind.Inbox;
        if (attributes.HasFlag(FolderAttributes.Trash)) return MailFolderKind.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return MailFolderKind.Junk;
        if (attributes.HasFlag(FolderAttributes.Sent)) return MailFolderKind.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return MailFolderKind.Drafts;
        if (attributes.HasFlag(FolderAttributes.Archive)) return MailFolderKind.Archive;
        if (attributes.HasFlag(FolderAttributes.Flagged)) return MailFolderKind.Flagged;
        if (attributes.HasFlag(FolderAttributes.Important)) return MailFolderKind.Important;
        if (attributes.HasFlag(FolderAttributes.All)) return MailFolderKind.All;

        return KindFromName(folder.Name, folder.FullName);
    }

    private static MailFolderKind KindFromName(string name, string fullName)
    {
        if (fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
            return MailFolderKind.Inbox;

        var n = name.ToLowerInvariant();
        if (n.Contains("корзин") || n == "trash") return MailFolderKind.Trash;
        if (n.Contains("спам") || n is "spam" or "junk") return MailFolderKind.Junk;
        if (n.Contains("отправлен") || n == "sent") return MailFolderKind.Sent;
        if (n.Contains("черновик") || n == "drafts") return MailFolderKind.Drafts;
        if (n.Contains("архив") || n == "archive") return MailFolderKind.Archive;

        return MailFolderKind.Other;
    }

    private static string DisplayName(IMailFolder folder, MailFolderKind kind) => kind switch
    {
        MailFolderKind.Inbox => "Входящие",
        MailFolderKind.Sent => "Отправленные",
        MailFolderKind.Drafts => "Черновики",
        MailFolderKind.Trash => "Корзина",
        MailFolderKind.Junk => "Спам",
        MailFolderKind.Archive => "Архив",
        MailFolderKind.Flagged => "Помеченные",
        MailFolderKind.Important => "Важные",
        MailFolderKind.All => "Все письма",
        _ => folder.Name
    };

    private static bool IsConnectionError(Exception ex) =>
        ex is IOException or ServiceNotConnectedException ||
        ex.InnerException is IOException;

    private static async Task WithTimeoutAsync(
        Func<CancellationToken, Task> action, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер Яндекса не ответил вовремя.");
        }
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await action(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер Яндекса не ответил вовремя.");
        }
    }

    private sealed class FolderSearchOutcome
    {
        public string Path { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public IList<UniqueId> Uids { get; init; } = new List<UniqueId>();
        public long Bytes { get; init; }
    }
}
