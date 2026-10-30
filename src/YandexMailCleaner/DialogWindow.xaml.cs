using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace YandexMailCleaner;

public enum DialogKind
{
    Info,
    Question,
    Warning,
    Error,
    Success,
    Empty
}

public partial class DialogWindow : Window
{
    private DialogWindow() => InitializeComponent();

    public static void ShowInfo(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, DialogKind.Info, false, "Понятно", null);

    public static void ShowSuccess(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, DialogKind.Success, false, "Отлично", null);

    public static void ShowWarning(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, DialogKind.Warning, false, "Понятно", null);

    public static void ShowError(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, DialogKind.Error, false, "Закрыть", null);

    public static void ShowEmpty(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, DialogKind.Empty, false, "Понятно", null);

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string confirmText = "Да",
        string cancelText = "Отмена")
        => ShowCore(owner, title, message, DialogKind.Question, true, confirmText, cancelText);

    private static bool ShowCore(
        Window? owner,
        string title,
        string message,
        DialogKind kind,
        bool withCancel,
        string okText,
        string? cancelText)
    {
        var window = new DialogWindow();
        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.OkButton.Content = okText;

        if (withCancel)
        {
            window.CancelButton.Visibility = Visibility.Visible;
            window.CancelButton.Content = cancelText ?? "Отмена";
        }

        ApplyKind(window, kind);

        if (owner is not null && owner.IsVisible)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true;
    }

    private static void ApplyKind(DialogWindow window, DialogKind kind)
    {
        var (glyph, foreground, background, fontFamily, bold) = kind switch
        {
            DialogKind.Warning => ("\uE7BA", "#B45309", "#FEF3C7", "Segoe MDL2 Assets", false),
            DialogKind.Error => ("\uEA39", "#B91C1C", "#FEE2E2", "Segoe MDL2 Assets", false),
            DialogKind.Success => ("\uE930", "#15803D", "#DCFCE7", "Segoe MDL2 Assets", false),
            DialogKind.Empty => ("\uE721", "#6B7280", "#F1F2F4", "Segoe MDL2 Assets", false),
            DialogKind.Question => ("?", "#C2410C", "#FFEDD5", "Segoe UI", true),
            _ => ("\uE946", "#1D4ED8", "#DBEAFE", "Segoe MDL2 Assets", false)
        };

        window.IconText.Text = glyph;
        window.IconText.FontFamily = new FontFamily(fontFamily);
        window.IconText.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        window.IconText.Foreground = Brush(foreground);
        window.IconBadge.Background = Brush(background);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // перетаскивание уже завершено
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
