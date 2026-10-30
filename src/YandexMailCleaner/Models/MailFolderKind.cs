namespace YandexMailCleaner.Models;

/// <summary>Назначение папки (по флагам IMAP \Inbox, \Trash, \Sent и т.д.).</summary>
public enum MailFolderKind
{
    Other = 0,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    Flagged,
    Important,
    All
}
