using System.IO;
using System.Text.Json;
using YandexMailCleaner.Models;

namespace YandexMailCleaner.Services;

/// <summary>
/// Хранит последние выбранные фильтры, чтобы не настраивать их заново.
/// Пароль здесь не хранится.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public CleanupOptions Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
                return new CleanupOptions();

            return JsonSerializer.Deserialize<CleanupOptions>(File.ReadAllText(AppPaths.SettingsFile))
                   ?? new CleanupOptions();
        }
        catch
        {
            return new CleanupOptions();
        }
    }

    public void Save(CleanupOptions options)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(options, Options));
        }
        catch
        {
            // настройки не критичны
        }
    }
}
