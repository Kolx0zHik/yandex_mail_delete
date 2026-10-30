using System.Windows;
using System.Windows.Threading;
using YandexMailCleaner.Services;

namespace YandexMailCleaner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Fatal(args.ExceptionObject as Exception);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Fatal(e.Exception);

        try
        {
            DialogWindow.ShowError(
                Application.Current?.MainWindow,
                "Непредвиденная ошибка",
                "Произошла непредвиденная ошибка:\n\n" + e.Exception.Message);
        }
        catch
        {
            MessageBox.Show(
                "Произошла непредвиденная ошибка:\n\n" + e.Exception.Message,
                "Яндекс.Почта — очистка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
