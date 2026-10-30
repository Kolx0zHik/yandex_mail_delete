using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MailKit.Security;
using YandexMailCleaner.Models;
using YandexMailCleaner.Services;

namespace YandexMailCleaner;

public partial class MainWindow : Window
{
    private static readonly int[] OlderThanMonths = { 1, 3, 6, 12 };

    private readonly ImapService _imap = new();
    private readonly CredentialStore _credentials = new();
    private readonly SettingsStore _settings = new();
    private readonly ObservableCollection<SelectableFolder> _folders = new();

    private bool _busy;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        FoldersItems.ItemsSource = _folders;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 24);
        Height = Math.Min(Height, area.Height - 24);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreFilterUi(_settings.Load());

        var saved = _credentials.Load();
        if (saved is null)
        {
            ShowLogin();
            return;
        }

        LoginEmail.Text = saved.Email;
        ShowLogin();
        await TryConnectAsync(saved.Email, saved.Password);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var email = LoginEmail.Text.Trim();
        var password = LoginPassword.Password;

        if (!IsValidYandexEmail(email))
        {
            LoginError.Text = "Введите адрес почты в домене yandex.ru, например name@yandex.ru.";
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            LoginError.Text = "Введите пароль приложения.";
            return;
        }

        LoginError.Text = "";
        if (await TryConnectAsync(email, password))
            LoginPassword.Clear();
    }

    private async Task<bool> TryConnectAsync(string email, string password)
    {
        if (_busy) return false;

        SetBusy(true, "Подключение к Яндекс.Почте…");
        try
        {
            await _imap.ConnectAsync(email, password);
            _credentials.Save(email, password);
            AppLogger.Info($"Подключение успешно: {email}");

            AccountText.Text = email;
            await LoadFoldersAsync();
            ShowMain();
            LoginError.Text = "";
            return true;
        }
        catch (AuthenticationException)
        {
            _credentials.Clear();
            AppLogger.Error("Ошибка аутентификации IMAP");
            LoginError.Text =
                "Неверный email или пароль приложения. Проверьте данные и убедитесь, " +
                "что в настройках Яндекс.Почты включён доступ по IMAP.";
            ShowLogin();
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка подключения IMAP", ex);
            LoginError.Text =
                "Не удалось подключиться к серверу Яндекса. Проверьте интернет и повторите попытку.\n" +
                ex.Message;
            ShowLogin();
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadFoldersAsync()
    {
        var list = await _imap.GetFoldersAsync();
        var last = _settings.Load();
        var preferred = new HashSet<string>(last.FolderPaths, StringComparer.OrdinalIgnoreCase);

        _folders.Clear();
        foreach (var info in list)
        {
            var selected = preferred.Count > 0
                ? preferred.Contains(info.Path)
                : info.Kind == MailFolderKind.Inbox;

            _folders.Add(new SelectableFolder(info.Path, info.DisplayName, info.Kind)
            {
                IsSelected = selected
            });
        }

        if (_folders.Count > 0 && _folders.All(f => !f.IsSelected))
        {
            var inbox = _folders.FirstOrDefault(f => f.Kind == MailFolderKind.Inbox) ?? _folders[0];
            inbox.IsSelected = true;
        }

        UpdateSelectedCount();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var options = BuildOptionsFromUi();

        if (options.FolderPaths.Count == 0)
        {
            StatusText.Text = "Отметьте хотя бы одну папку.";
            return;
        }

        if (options.DateMode == DateFilterMode.Range &&
            options.RangeFrom is null && options.RangeTo is null)
        {
            StatusText.Text = "Укажите хотя бы одну дату периода.";
            return;
        }

        if (options.DateMode == DateFilterMode.Range &&
            options.RangeFrom is { } from && options.RangeTo is { } to && from > to)
        {
            StatusText.Text = "Дата начала периода больше даты окончания.";
            return;
        }

        if (options.SizeMode == SizeFilterMode.LargerThan && options.MinSizeBytes <= 0)
        {
            StatusText.Text = "Укажите размер в мегабайтах (больше нуля).";
            return;
        }

        _settings.Save(options);
        AppLogger.Info(
            $"Старт очистки: папки=[{string.Join(", ", options.FolderPaths)}], " +
            $"дата={options.DateMode}, месяцев={options.OlderThanMonths}, " +
            $"размер={options.SizeMode} {options.MinSizeBytes}");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetBusy(true, "Поиск писем…");
        OperationProgress.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = true;
        StatusText.Text = "Идёт поиск писем…";

        var progress = new Progress<string>(text =>
        {
            StatusText.Text = text;
            BusyText.Text = text;
        });

        try
        {
            var plan = await _imap.BuildPlanAsync(options, progress, ct);
            AppLogger.Info($"План готов: писем {plan.TotalCount}");

            if (plan.TotalCount == 0)
            {
                StatusText.Text = "Подходящих писем не найдено.";
                OperationProgress.IsIndeterminate = false;
                OperationProgress.Visibility = Visibility.Collapsed;
                SetBusy(false);
                DialogWindow.ShowEmpty(
                    this,
                    "Писем не найдено",
                    "По выбранным папкам, периоду и размеру подходящих писем не обнаружено.\n\n" +
                    "Попробуйте изменить период, отметить больше папок или снять ограничение по размеру.");
                return;
            }

            SetBusy(false);

            var planDetails = plan.TotalBytes > 0
                ? $"Найдено писем: {plan.TotalCount} (~{SizeFormatter.Format(plan.TotalBytes)})."
                : $"Найдено писем: {plan.TotalCount}.";

            var confirmed = DialogWindow.Confirm(
                this,
                "Подтверждение",
                planDetails + "\n\nПереместить их в Корзину?",
                "Переместить",
                "Отмена");

            if (!confirmed)
            {
                StatusText.Text = "Действие отменено. Ничего не удалено.";
                return;
            }

            SetBusy(true, "Перемещение в Корзину…");
            StatusText.Text = "Перемещение в Корзину…";

            var result = await _imap.ExecutePlanAsync(plan, progress, ct);
            AppLogger.Info($"Готово: перемещено {result.MovedCount}, папок с ошибкой {result.FailedFolders}");

            var summary = result.MovedBytes > 0
                ? $"Готово. Перемещено писем: {result.MovedCount}, освобождено ~{SizeFormatter.Format(result.MovedBytes)}."
                : $"Готово. Перемещено писем: {result.MovedCount}.";

            if (result.FailedFolders > 0)
                summary += $"\nНе удалось обработать папок: {result.FailedFolders}. Подробности в журнале.";

            StatusText.Text = summary;

            if (result.FailedFolders > 0)
            {
                DialogWindow.ShowWarning(this, "Очистка завершена с замечаниями", summary);
            }
            else
            {
                DialogWindow.ShowSuccess(this, "Готово", summary);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Операция отменена.";
            AppLogger.Info("Операция отменена пользователем");
        }
        catch (TimeoutException)
        {
            StatusText.Text = "Сервер Яндекса не ответил вовремя. Попробуйте ещё раз.";
            AppLogger.Error("Тайм-аут операции IMAP");
            DialogWindow.ShowWarning(
                this,
                "Сервер не ответил",
                "Сервер Яндекса не ответил вовремя.\n\n" +
                "Попробуйте ещё раз. Если повторяется — выберите меньше папок или другой период.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка очистки", ex);
            StatusText.Text = "Ошибка: " + ex.Message;
            DialogWindow.ShowError(
                this,
                "Ошибка очистки",
                "Не удалось выполнить очистку:\n\n" + ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            OperationProgress.IsIndeterminate = false;
            OperationProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void CancelBusy_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BusyText.Text = "Отмена…";
    }

    private async void ChangeAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var confirmed = DialogWindow.Confirm(
            this,
            "Смена аккаунта",
            "Отключиться от текущего ящика и ввести другие данные?",
            "Сменить",
            "Отмена");

        if (!confirmed) return;

        SetBusy(true, "Отключение…");
        try
        {
            await _imap.DisconnectAsync();
        }
        catch
        {
            // уже отключены
        }

        _credentials.Clear();
        _folders.Clear();
        UpdateSelectedCount();
        AccountText.Text = "";
        LoginPassword.Clear();
        StatusText.Text = "";
        ShowLogin();
        SetBusy(false);
        LoginEmail.Focus();
    }

    private void HowToLink_Click(object sender, RoutedEventArgs e)
    {
        DialogWindow.ShowInfo(
            this,
            "Пароль приложения",
            "Как получить пароль приложения:\n\n" +
            "1. Откройте https://id.yandex.ru и войдите в аккаунт.\n" +
            "2. Перейдите в раздел «Безопасность» → «Пароли приложений».\n" +
            "3. Создайте пароль для «Почты» и скопируйте его.\n" +
            "4. В настройках Почты (mail.yandex.ru → Все настройки → Почтовые программы) " +
            "включите доступ по протоколу IMAP.\n\n" +
            "В это приложение вводится именно пароль приложения, а не обычный пароль аккаунта.");
    }

    private void FolderCheck_Changed(object sender, RoutedEventArgs e) => UpdateSelectedCount();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in _folders)
            folder.IsSelected = true;
        UpdateSelectedCount();
    }

    private void UnselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var folder in _folders)
            folder.IsSelected = false;
        UpdateSelectedCount();
    }

    private void DateModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangePanel is null) return;
        RangePanel.Visibility = DateModeCombo.SelectedIndex == 5
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizePanel is null) return;
        SizePanel.Visibility = SizeCombo.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RestoreFilterUi(CleanupOptions options)
    {
        DateModeCombo.SelectedIndex = options.DateMode switch
        {
            DateFilterMode.AllTime => 4,
            DateFilterMode.Range => 5,
            _ => options.OlderThanMonths switch
            {
                1 => 0,
                3 => 1,
                6 => 2,
                12 => 3,
                _ => 2
            }
        };

        DateFrom.SelectedDate = options.RangeFrom;
        DateTo.SelectedDate = options.RangeTo;

        SizeCombo.SelectedIndex = options.SizeMode == SizeFilterMode.LargerThan ? 1 : 0;
        SizeMb.Text = options.MinSizeBytes > 0
            ? (options.MinSizeBytes / 1024d / 1024d).ToString("0.##", CultureInfo.CurrentCulture)
            : "10";

        RangePanel.Visibility = DateModeCombo.SelectedIndex == 5
            ? Visibility.Visible
            : Visibility.Collapsed;
        SizePanel.Visibility = SizeCombo.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private CleanupOptions BuildOptionsFromUi()
    {
        var options = new CleanupOptions
        {
            FolderPaths = _folders.Where(f => f.IsSelected).Select(f => f.Path).ToList()
        };

        var dateIndex = DateModeCombo.SelectedIndex;
        if (dateIndex == 5)
        {
            options.DateMode = DateFilterMode.Range;
            options.RangeFrom = DateFrom.SelectedDate?.Date;
            options.RangeTo = DateTo.SelectedDate?.Date;
        }
        else if (dateIndex == 4)
        {
            options.DateMode = DateFilterMode.AllTime;
        }
        else
        {
            options.DateMode = DateFilterMode.OlderThan;
            options.OlderThanMonths =
                dateIndex >= 0 && dateIndex < OlderThanMonths.Length ? OlderThanMonths[dateIndex] : 6;
        }

        if (SizeCombo.SelectedIndex == 1)
        {
            options.SizeMode = SizeFilterMode.LargerThan;
            if (double.TryParse(
                    SizeMb.Text.Replace(',', '.'),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var megabytes) && megabytes > 0)
            {
                options.MinSizeBytes = (long)(megabytes * 1024d * 1024d);
            }
        }

        return options;
    }

    private void UpdateSelectedCount() =>
        SelectedCountText.Text = _folders.Count(f => f.IsSelected).ToString();

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(text))
            BusyText.Text = text;

        ConnectButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy;
        ChangeAccountButton.IsEnabled = !busy;
        CancelBusyButton.IsEnabled = busy;
    }

    private void ShowLogin()
    {
        LoginPanel.Visibility = Visibility.Visible;
        MainPanel.Visibility = Visibility.Collapsed;
        LoginPassword.Clear();
    }

    private void ShowMain()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
    }

    private static bool IsValidYandexEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;

        var domain = email[(at + 1)..].Trim().TrimEnd('.');
        return domain.Equals("yandex.ru", StringComparison.OrdinalIgnoreCase);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = _imap.DisconnectAsync();
    }
}
