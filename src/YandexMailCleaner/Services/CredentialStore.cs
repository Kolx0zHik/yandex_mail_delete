using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YandexMailCleaner.Services;

/// <summary>
/// Хранит email и пароль приложения, зашифрованные DPAPI в рамках текущего
/// пользователя Windows. Пароль сохраняется один раз и больше не запрашивается.
/// При копировании exe на другой ПК не переносится (шифрование привязано к профилю).
/// </summary>
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YandexMailCleaner.DPAPI.v1");

    public sealed record Stored(string Email, string Password);

    private sealed class Dto
    {
        public string E { get; set; } = "";
        public string P { get; set; } = "";
    }

    public Stored? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.AccountFile))
                return null;

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(AppPaths.AccountFile));
            if (dto is null || string.IsNullOrEmpty(dto.E) || string.IsNullOrEmpty(dto.P))
                return null;

            var cipher = Convert.FromBase64String(dto.P);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return new Stored(dto.E, Encoding.UTF8.GetString(plain));
        }
        catch
        {
            return null;
        }
    }

    public void Save(string email, string password)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);

        var dto = new Dto { E = email, P = Convert.ToBase64String(cipher) };
        File.WriteAllText(AppPaths.AccountFile, JsonSerializer.Serialize(dto));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.AccountFile))
                File.Delete(AppPaths.AccountFile);
        }
        catch
        {
            // игнорируем
        }
    }
}
