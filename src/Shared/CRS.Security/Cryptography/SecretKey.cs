using System.Security.Cryptography;
namespace CRS.Security.Cryptography;

/// <summary>受控密钥材料，释放时清零；不承诺托管运行时彻底擦除所有瞬态副本。</summary>
public sealed class SecretKey : IDisposable
{
    private byte[]? bytes;
    public SecretKey(ReadOnlySpan<byte> value) {if(value.Length!=32) throw new CryptographicException("密钥无效。"); bytes=value.ToArray();}
    public byte[] Copy()=>bytes?.ToArray()??throw new ObjectDisposedException(nameof(SecretKey));
    public SecretKey Clone()=>new(bytes??throw new ObjectDisposedException(nameof(SecretKey)));
    public void Dispose() {if(bytes is {} value) CryptographicOperations.ZeroMemory(value); bytes=null;}
}
