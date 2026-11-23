using System.Security.Cryptography;
using System.Text;
namespace CRS.Security.Cryptography;

public sealed record SealedPayload(int Version,Guid Id,byte[] Nonce,byte[] Ciphertext,byte[] Tag);
/// <summary>固定用途隔离，旧备份编码保持不变，导出容器不得当作备份解密。</summary>
public enum EncryptionPurpose {Backup,Export}
/// <summary>加密正文的纯机制，HKDF 用途隔离与认证；容器序列化和文件 I/O 属于 Infrastructure。</summary>
public static class AuthenticatedEncryption
{
    public static SealedPayload Seal(ReadOnlySpan<byte> payload,SecretKey key,Guid id,EncryptionPurpose purpose=EncryptionPurpose.Backup)
    {
        if(id==Guid.Empty) throw new CryptographicException("容器标识无效。");
        var bytes=Derive(key,id,purpose); var nonce=RandomNumberGenerator.GetBytes(12); var ciphertext=new byte[payload.Length]; var tag=new byte[16];
        try {using var aes=new AesGcm(bytes,16); aes.Encrypt(nonce,payload,ciphertext,tag,Aad(id,purpose)); return new(1,id,nonce,ciphertext,tag);}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    public static byte[] Open(SealedPayload payload,SecretKey key,EncryptionPurpose purpose=EncryptionPurpose.Backup)
    {
        if(payload.Version!=1 || payload.Id==Guid.Empty || payload.Nonce is null || payload.Nonce.Length!=12 || payload.Tag is null || payload.Tag.Length!=16 || payload.Ciphertext is null) throw new CryptographicException("备份格式不支持。");
        var bytes=Derive(key,payload.Id,purpose); var plain=new byte[payload.Ciphertext.Length];
        try {using var aes=new AesGcm(bytes,16); aes.Decrypt(payload.Nonce,payload.Ciphertext,payload.Tag,plain,Aad(payload.Id,purpose)); return plain;}
        catch {CryptographicOperations.ZeroMemory(plain); throw new CryptographicException("备份认证失败。");}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    private static byte[] Derive(SecretKey key,Guid id,EncryptionPurpose purpose)
    {
        var raw=key.Copy();
        try {return HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,id.ToByteArray(),Encoding.UTF8.GetBytes($"CRS.{Name(purpose)}.Key.v1"));}
        finally {CryptographicOperations.ZeroMemory(raw);}
    }
    private static string Name(EncryptionPurpose purpose)=>purpose switch {EncryptionPurpose.Backup=>"Backup",EncryptionPurpose.Export=>"Export",_=>throw new CryptographicException("加密用途不支持。")};
    private static byte[] Aad(Guid id,EncryptionPurpose purpose)=>Encoding.UTF8.GetBytes($"CRS.{Name(purpose)}.v1|{id:D}");
}
