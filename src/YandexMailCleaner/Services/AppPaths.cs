using System.IO;

namespace YandexMailCleaner.Services;

/// <summary>
/// Пути к локальным данным приложения (на каждого пользователя Windows).
/// </summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YandexMailCleaner");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string AccountFile => Path.Combine(DataDir, "account.dat");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
}
