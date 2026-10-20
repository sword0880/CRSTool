using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace CRS.Worker.Service;

/// <summary>独立服务密钥由机器 DPAPI 保护，再以目录 ACL 限定读取者；不依赖桌面用户配置文件。</summary>
public static class ServiceKey
{
    private sealed record Envelope(int Version, string IdentitySid, byte[] ProtectedKey);
    private static readonly byte[] Entropy = "CRS.Background.Service/key/v1"u8.ToArray();
    public static byte[] Open(string directory)
    {
        var path = Path.Combine(directory, "service-key.dpapi");
        StorageSecurity.RejectLinks(path); // 不能通过密钥文件链接读取保护目录以外的机器密钥。
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("密钥文件格式错误。");
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("密钥文件损坏。");
            if (envelope.Version != 1 || envelope.IdentitySid != sid)
                throw new InvalidOperationException("服务身份已改变，必须显式迁移密钥；禁止自动生成新密钥。");
            var key = ProtectedData.Unprotect(envelope.ProtectedKey, Entropy, DataProtectionScope.LocalMachine);
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException("密钥长度错误。"); }
            return key;
        }
        // 现有数据缺钥时必须失败；不接受旧明文库或以新密钥静默覆盖。
        if (Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("数据库存在但服务密钥缺失，需要恢复密钥。");
        var created = RandomNumberGenerator.GetBytes(32);
        try
        {
            var envelope = new Envelope(1, sid, ProtectedData.Protect(created, Entropy, DataProtectionScope.LocalMachine));
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(JsonSerializer.SerializeToUtf8Bytes(envelope)); file.Flush(true);
            return created;
        }
        catch { CryptographicOperations.ZeroMemory(created); throw; }
    }
}
