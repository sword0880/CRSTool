using System.Security.Cryptography;
using System.Text;

namespace CRS.Security.Authentication;

/// <summary>手机恢复码独立于数据库恢复密钥；128 位随机值只存带盐的用途隔离哈希。</summary>
public static class MfaRecoveryCodes
{
    public static string Generate()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public static byte[] Hash(string code,byte[] salt)
    {
        var normalized=code.Replace("-","").Replace(" ","").ToUpperInvariant();
        if(normalized.Length!=32 || normalized.Any(c=>!Uri.IsHexDigit(c)) || salt.Length!=16) throw new CryptographicException("恢复码格式无效。");
        var input=Encoding.ASCII.GetBytes("CRS-RECOVERY-CODE-V1|"+Convert.ToHexString(salt)+"|"+normalized);
        try {return SHA256.HashData(input);} finally {CryptographicOperations.ZeroMemory(input);}
    }
}
