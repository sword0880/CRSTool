using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CRS.AI.Models.Configuration;

/// <summary>原子保存默认模型和各供应商密钥。密钥使用当前 Windows 用户的 DPAPI 加密。</summary>
internal sealed class AiModelSettingsStore(string? localDataRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path = Path.Combine(localDataRoot ??
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRS", "AI", "models.json");

    public AiModelSettings Load() => ToSettings(Read());

    public AiModelSettings Save(string provider, string model, string? newKey, CancellationToken token)
    {
        AiModelSettings.ValidateSelection(provider, model);
        if (newKey is not null) AiModelSettings.ValidateKey(newKey);
        return Update(current =>
        {
            var key = newKey is null ? current.Profiles.GetValueOrDefault(provider)?.ProtectedKey : Protect(provider, newKey);
            if (string.IsNullOrEmpty(key)) throw new AiModelException("ApiKeyMissing");
            current.Profiles[provider] = new(model, key);
            return current with { DefaultProvider = provider };
        }, token);
    }

    public AiModelSettings Remove(string provider, CancellationToken token) => Update(current =>
    {
        current.Profiles.Remove(provider);
        return current.DefaultProvider == provider ? current with { DefaultProvider = null } : current;
    }, token);

    public string ReadKey(string provider)
    {
        var profile = Read().Profiles.GetValueOrDefault(provider);
        return profile is null ? throw new AiModelException("ApiKeyMissing") : Unprotect(provider, profile.ProtectedKey);
    }

    public (string Provider, string Model, string Key) ReadDefault()
    {
        // 一次读取确定供应商、模型及密钥，避免并发切换时混用凭证。
        var current = Read();
        if (current.DefaultProvider is not { } provider || !current.Profiles.TryGetValue(provider, out var profile))
            throw new AiModelException("DefaultModelMissing");
        AiModelSettings.ValidateSelection(provider, profile.Model);
        return (provider, profile.Model, Unprotect(provider, profile.ProtectedKey));
    }

    private AiModelSettings Update(Func<StoredSettings, StoredSettings> update, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 独占锁覆盖读取、修改与替换，避免另一个 CRS 进程覆盖已保存的其他供应商。
            using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var current = update(Read());
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(current, Json));
                token.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); }
            return ToSettings(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AiModelException("SettingsWriteFailed");
        }
    }

    private StoredSettings Read()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 64 * 1024) throw new AiModelException("SettingsReadFailed");
            var current = JsonSerializer.Deserialize<StoredSettings>(stream, Json);
            return current is { Profiles: not null } && current.Profiles.Values.All(profile =>
                profile is { Model.Length: > 0, ProtectedKey.Length: > 0 })
                ? current : throw new AiModelException("SettingsReadFailed");
        }
        catch (FileNotFoundException) { return new(null, new(StringComparer.Ordinal)); }
        catch (DirectoryNotFoundException) { return new(null, new(StringComparer.Ordinal)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new AiModelException("SettingsReadFailed"); }
    }

    private static AiModelSettings ToSettings(StoredSettings current) =>
        new(current.DefaultProvider, current.Profiles.ToDictionary(
            item => item.Key, item => new AiProviderSetting(item.Value.Model, true), StringComparer.Ordinal));

    private static string Protect(string provider, string key)
    {
        var plaintext = Encoding.UTF8.GetBytes(key);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plaintext, Entropy(provider), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { throw new AiModelException("ApiKeyProtectFailed"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string Unprotect(string provider, string value)
    {
        try
        {
            var plaintext = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy(provider),
                DataProtectionScope.CurrentUser);
            try
            {
                var key = Encoding.UTF8.GetString(plaintext);
                AiModelSettings.ValidateKey(key);
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new AiModelException("ApiKeyUnreadable"); }
    }

    // 用途标记和供应商标识绑定密文，避免把一个供应商的密钥作为另一个的凭证解密。
    private static byte[] Entropy(string provider) => Encoding.UTF8.GetBytes("CRS.AI.ModelKey.v1:" + provider);

    private sealed record StoredSettings(string? DefaultProvider, Dictionary<string, StoredProfile> Profiles);
    private sealed record StoredProfile(string Model, string ProtectedKey);
}
