using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CRS.Security.Cryptography;

/// <summary>版本一参数，内存单位为 KiB；未知参数在分配内存前拒绝。</summary>
public sealed record KdfParameters(int MemoryKiB=65536,int Iterations=3,int Parallelism=2);
/// <summary>只实现密码派生和参数约束，不认识保险库文件或 SQLCipher。</summary>
public static class KeyDerivation
{
    public static byte[] Derive(string password,byte[] salt,KdfParameters parameters)
    {
        if(password is null || password.Length>1024 || salt.Length!=16 || parameters!=new KdfParameters()) throw new CryptographicException("不支持的密码派生参数。");
        var bytes=Encoding.UTF8.GetBytes(password);
        try {using var argon=new Argon2id(bytes) {Salt=salt,MemorySize=parameters.MemoryKiB,Iterations=parameters.Iterations,DegreeOfParallelism=parameters.Parallelism}; return argon.GetBytes(32);}
        finally {CryptographicOperations.ZeroMemory(bytes);}
    }
    public static void ValidatePassword(string password) {if(password is null || password.Length is <12 or >1024) throw new CryptographicException("主密码需至少 12 个字符，最多 1024 个字符。");}
}
