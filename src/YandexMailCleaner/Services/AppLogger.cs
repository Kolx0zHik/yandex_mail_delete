using System.IO;

namespace YandexMailCleaner.Services;

/// <summary>
/// Простой файловый журнал операций. Пишется в %AppData%\YandexMailCleaner\logs.
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void Fatal(Exception? ex) =>
        Write("FATAL", ex?.ToString() ?? "unknown");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var file = Path.Combine(AppPaths.LogsDir, $"cleaner-{DateTime.Now:yyyyMMdd}.log");
            lock (Lock)
            {
                File.AppendAllText(file,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Журнал не должен ломать основную логику.
        }
    }
}
