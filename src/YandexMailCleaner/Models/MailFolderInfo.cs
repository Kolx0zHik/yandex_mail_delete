namespace YandexMailCleaner.Models;

/// <summary>Папка почтового ящика, полученная с сервера.</summary>
public sealed class MailFolderInfo
{
    public string Path { get; init; } = "";

    public string DisplayName { get; init; } = "";

    public MailFolderKind Kind { get; init; }
}
