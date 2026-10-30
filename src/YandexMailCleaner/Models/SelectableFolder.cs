using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YandexMailCleaner.Models;

/// <summary>Папка в списке с галочкой (для привязки в UI).</summary>
public sealed class SelectableFolder : INotifyPropertyChanged
{
    private bool _isSelected;

    public SelectableFolder(string path, string displayName, MailFolderKind kind)
    {
        Path = path;
        DisplayName = displayName;
        Kind = kind;
    }

    public string Path { get; }

    public string DisplayName { get; }

    public MailFolderKind Kind { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
