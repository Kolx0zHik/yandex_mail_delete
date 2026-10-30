using MailKit;

namespace YandexMailCleaner.Models;

/// <summary>План по одной папке: какие письма и сколько они занимают.</summary>
public sealed class FolderPlan
{
    public string Path { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public IList<UniqueId> Uids { get; init; } = new List<UniqueId>();

    public long Bytes { get; init; }
}

/// <summary>Итог поиска по всем выбранным папкам.</summary>
public sealed class CleanupPlan
{
    public IReadOnlyList<FolderPlan> Folders { get; init; } = Array.Empty<FolderPlan>();

    public int TotalCount => Folders.Sum(f => f.Uids.Count);

    public long TotalBytes => Folders.Sum(f => f.Bytes);
}

/// <summary>Итог перемещения в Корзину.</summary>
public sealed class CleanupResult
{
    public int MovedCount { get; init; }

    public long MovedBytes { get; init; }

    public int FailedFolders { get; init; }
}
