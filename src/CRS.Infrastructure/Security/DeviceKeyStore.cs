using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Security.Cryptography;

namespace CRS.Infrastructure;

/// <summary>仅保管当前 Windows 用户可解封的 DEK；不保存主密码，不作为唯一恢复凭证。</summary>
internal static class DeviceKeyStore
{
    internal static string PathFor(string directory)=>Path.Combine(directory,"vault.device");
    private sealed record DeviceSlot(int Version,Guid VaultId,byte[] ProtectedKey);
    private static byte[] Entropy(Guid vaultId)=>Encoding.UTF8.GetBytes("CRS.LocalKey.v1|"+vaultId.ToString("D"));
    internal static SecretKey Read(string directory,Guid vaultId)
    {
        if(!OperatingSystem.IsWindows()) throw new CrsException("本机密钥保管仅支持 Windows，请使用主密码恢复入口。");
        byte[]? raw=null;
        try
        {
            using var stream=File.OpenRead(PathFor(directory));
            if(stream.Length is <=0 or >16384) throw new CrsException("本机密钥文件格式无效，请使用主密码重新启用本机登录。");
            var slot=JsonSerializer.Deserialize<DeviceSlot>(stream,VaultFiles.Options)??throw new JsonException();
            if(slot.Version!=1 || slot.VaultId!=vaultId || slot.ProtectedKey is null || slot.ProtectedKey.Length is <=0 or >8192) throw new CrsException("本机密钥与保险库身份不一致。");
            raw=ProtectedData.Unprotect(slot.ProtectedKey,Entropy(vaultId),DataProtectionScope.CurrentUser);
            return new SecretKey(raw);
        }
        catch(Exception error) when(error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {throw new CrsException("本机密钥不可用，请用主密码或数据库恢复密钥重新启用；不会回退明文数据库。");}
        finally {if(raw is not null) CryptographicOperations.ZeroMemory(raw);}
    }
    internal static void Save(string directory,Guid vaultId,SecretKey key)
    {
        if(!OperatingSystem.IsWindows()) throw new CrsException("本机密钥保管仅支持 Windows。");
        var raw=key.Copy();
        try
        {
            var wrapped=ProtectedData.Protect(raw,Entropy(vaultId),DataProtectionScope.CurrentUser);
            // 只在完整手机认证后提交受保护槽；日常锁定仍销毁内存中的业务会话。
            VaultFiles.AtomicWrite(PathFor(directory),JsonSerializer.SerializeToUtf8Bytes(new DeviceSlot(1,vaultId,wrapped)),true);
        }
        catch(CryptographicException) {throw new CrsException("Windows 无法保管本机密钥，请检查当前用户配置。保险库未开放业务访问。");}
        finally {CryptographicOperations.ZeroMemory(raw);}
    }
}
