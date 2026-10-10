using System.Security.Cryptography;

namespace CRS.Security.Cryptography;

public sealed record WrappedKey(byte[] Nonce,byte[] Ciphertext,byte[] Tag);
/// <summary>AES-256-GCM 密钥封装机制，每次产生新 Nonce，调用方必须提供经过版本化的认证上下文。</summary>
public static class KeyWrapping
{
    public static WrappedKey Wrap(ReadOnlySpan<byte> key,ReadOnlySpan<byte> kek,ReadOnlySpan<byte> authentication)
    {
        if(key.Length!=32 || kek.Length!=32) throw new CryptographicException("密钥长度无效。");
        var nonce=RandomNumberGenerator.GetBytes(12); var ciphertext=new byte[32]; var tag=new byte[16];
        using var aes=new AesGcm(kek,16); aes.Encrypt(nonce,key,ciphertext,tag,authentication); return new(nonce,ciphertext,tag);
    }
    public static SecretKey Unwrap(WrappedKey wrapped,ReadOnlySpan<byte> kek,ReadOnlySpan<byte> authentication)
    {
        if(wrapped.Nonce.Length!=12 || wrapped.Tag.Length!=16 || wrapped.Ciphertext.Length!=32 || kek.Length!=32) throw new CryptographicException("密钥封装无效。");
        Span<byte> plain=stackalloc byte[32];
        try {using var aes=new AesGcm(kek,16); aes.Decrypt(wrapped.Nonce,wrapped.Ciphertext,wrapped.Tag,plain,authentication); return new(plain);}
        finally {CryptographicOperations.ZeroMemory(plain);}
    }
}
