using System.Security.Cryptography;
using System.Text;
namespace CRS.Security.Cryptography;

public sealed record SealedPayload(int Version,Guid Id,byte[] Nonce,byte[] Ciphertext,byte[] Tag);
/// <summary>加密正文的纯机制，HKDF 用途隔离与认证；容器序列化和文件 I/O 属于 Infrastructure。</summary>
public static class AuthenticatedEncryption
{
    public static SealedPayload Seal(ReadOnlySpan<byte> payload,SecretKey key,Guid id)
    {
        if(id==Guid.Empty) throw new CryptographicException("容器标识无效。");
        var bytes=Derive(key,id); var nonce=RandomNumberGenerator.GetBytes(12); var ciphertext=new byte[payload.Length]; var tag=new byte[16];
        try {using var aes=new AesGcm(bytes,16); aes.Encrypt(nonce,payload,ciphertext,tag,Aad(id)); return new(1,id,nonce,ciphertext,tag);}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    public static byte[] Open(SealedPayload payload,SecretKey key)
    {
        if(payload.Version!=1 || payload.Id==Guid.Empty || payload.Nonce is null || payload.Nonce.Length!=12 || payload.Tag is null || payload.Tag.Length!=16 || payload.Ciphertext is null) throw new CryptographicException("备份格式不支持。");
        var bytes=Derive(key,payload.Id); var plain=new byte[payload.Ciphertext.Length];
        try {using var aes=new AesGcm(bytes,16); aes.Decrypt(payload.Nonce,payload.Ciphertext,payload.Tag,plain,Aad(payload.Id)); return plain;}
        catch {CryptographicOperations.ZeroMemory(plain); throw new CryptographicException("备份认证失败。");}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    private static byte[] Derive(SecretKey key,Guid id)
    {
        var raw=key.Copy();
        try {return HKDF.DeriveKey(HashAlgorithmName.SHA256,raw,32,id.ToByteArray(),Encoding.UTF8.GetBytes("CRS.Backup.Key.v1"));}
        finally {CryptographicOperations.ZeroMemory(raw);}
    }
    private static byte[] Aad(Guid id)=>Encoding.UTF8.GetBytes($"CRS.Backup.v1|{id:D}");
}
