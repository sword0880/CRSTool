using System.Security.Cryptography;
namespace CRS.Security.Cryptography;

public sealed record PasswordPayload(int Version,byte[] Salt,KdfParameters Parameters,SealedPayload Body);
/// <summary>独立导出密码的纯机制，不读取数据库密钥，不进行文件操作。</summary>
public static class PasswordEncryption
{
    public static PasswordPayload Seal(ReadOnlySpan<byte> plain,string password)
    {
        ValidatePassword(password); var salt=RandomNumberGenerator.GetBytes(16); var parameters=new KdfParameters();
        var raw=KeyDerivation.Derive(password,salt,parameters);
        try {using var key=new SecretKey(raw); return new(1,salt,parameters,AuthenticatedEncryption.Seal(plain,key,Guid.NewGuid(),EncryptionPurpose.Export));}
        finally {CryptographicOperations.ZeroMemory(raw);}
    }
    public static byte[] Open(PasswordPayload payload,string password)
    {
        ValidatePassword(password);
        // 在 Argon2 内存分配前拒绝未知参数和格式，避免容器指定任意工作量。
        if(payload.Version!=1 || payload.Salt is not {Length:16} || payload.Parameters!=new KdfParameters() || payload.Body is null
            || payload.Body.Version!=1 || payload.Body.Id==Guid.Empty || payload.Body.Nonce is not {Length:12} || payload.Body.Tag is not {Length:16})
            throw new CryptographicException("加密导出格式不支持。");
        var raw=KeyDerivation.Derive(password,payload.Salt,payload.Parameters);
        try {using var key=new SecretKey(raw); return AuthenticatedEncryption.Open(payload.Body,key,EncryptionPurpose.Export);}
        finally {CryptographicOperations.ZeroMemory(raw);}
    }
    /// <summary>构建工作簿前先校验密码，未知参数在派生前拒绝。</summary>
    public static void ValidatePassword(string password) {if(password is null || password.Length is <12 or >1024) throw new CryptographicException("导出密码需为 12—1024 个字符。");}
}
