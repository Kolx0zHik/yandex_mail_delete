namespace YandexMailCleaner.Models;

public enum DateFilterMode
{
    AllTime = 0,
    OlderThan = 1,
    Range = 2
}

public enum SizeFilterMode
{
    Any = 0,
    LargerThan = 1
}

/// <summary>
/// Параметры очистки: папки, период, размер. Сериализуется в settings.json.
/// </summary>
public sealed class CleanupOptions
{
    public List<string> FolderPaths { get; set; } = new();

    public DateFilterMode DateMode { get; set; } = DateFilterMode.OlderThan;

    /// <summary>Сколько месяцев назад считать «старше» (1, 3, 6, 12).</summary>
    public int OlderThanMonths { get; set; } = 6;

    public DateTime? RangeFrom { get; set; }

    public DateTime? RangeTo { get; set; }

    public SizeFilterMode SizeMode { get; set; } = SizeFilterMode.Any;

    public long MinSizeBytes { get; set; }
}
