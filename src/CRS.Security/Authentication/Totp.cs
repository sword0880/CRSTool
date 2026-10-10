using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CRS.Security.Authentication;

/// <summary>RFC6238 的 SHA1/六位/30 秒配置；纯算法不访问时钟、文件或数据库。</summary>
public static class Totp
{
    public static byte[] GenerateSecret()=>RandomNumberGenerator.GetBytes(20);
    public static string Compute(byte[] secret,long step,int digits=6)
    {
        if(secret.Length!=20 || step<0 || digits is not (6 or 8)) throw new CryptographicException("验证码参数无效。");
        Span<byte> counter=stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter,step);
        Span<byte> digest=stackalloc byte[20]; HMACSHA1.HashData(secret,counter,digest);
        var offset=digest[19]&15; var number=BinaryPrimitives.ReadUInt32BigEndian(digest.Slice(offset,4))&0x7fffffff;
        CryptographicOperations.ZeroMemory(digest);
        return (number%(digits==6?1_000_000u:100_000_000u)).ToString("D"+digits,CultureInfo.InvariantCulture);
    }
    public static long? Match(byte[] secret,string code,long unixSeconds,long? lastStep)
    {
        if(code.Length!=6 || code.Any(c=>c<'0'||c>'9') || unixSeconds<0) return null;
        var input=Encoding.ASCII.GetBytes(code); long? matched=null;
        // 固定比较整个窗口；已消费步骤和系统时间回退不能获得再次放行。
        foreach(var delta in new[]{0,-1,1})
        {
            var step=unixSeconds/30+delta; if(step<0) continue;
            var expected=Encoding.ASCII.GetBytes(Compute(secret,step));
            if(CryptographicOperations.FixedTimeEquals(input,expected) && step>(lastStep??-1) && matched is null) matched=step;
            CryptographicOperations.ZeroMemory(expected);
        }
        CryptographicOperations.ZeroMemory(input); return matched;
    }
    public static string EnrollmentUri(byte[] secret,Guid vaultId)
    {
        if(secret.Length!=20) throw new CryptographicException("绑定参数无效。");
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var encoded=new StringBuilder(32); int bits=0,value=0;
        foreach(var b in secret) {value=(value<<8)|b; bits+=8; while(bits>=5) {bits-=5; encoded.Append(alphabet[(value>>bits)&31]);}}
        var label="CRS Desktop:"+vaultId.ToString("N")[..12];
        return "otpauth://totp/"+Uri.EscapeDataString(label)+"?secret="+encoded+"&issuer=CRS%20Desktop&algorithm=SHA1&digits=6&period=30";
    }
}
