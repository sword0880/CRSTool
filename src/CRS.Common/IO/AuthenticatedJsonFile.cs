using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Serialization;
namespace CRS.IO;
/// <summary>已有密钥和明确用途下的有界认证JSON文件；不生成密钥，不承担业务授权或恢复策略。</summary>
public static class AuthenticatedJsonFile
{
    private const int Limit=256*1024;
    private sealed record Envelope(int Version,byte[] Body,byte[] Tag);
    private static byte[] Tag(ReadOnlySpan<byte> key,string purpose,byte[] body)
    {
        if(key.Length!=32||purpose.Length is <1 or >160||purpose.Any(c=>c<' '||c>'~'))throw new InvalidDataException("认证文件密钥或用途无效。");
        var prefix=Encoding.UTF8.GetBytes("CRS.AuthenticatedJson/v1/"+purpose+"\0");var bytes=new byte[prefix.Length+body.Length];prefix.CopyTo(bytes,0);body.CopyTo(bytes,prefix.Length);return HMACSHA256.HashData(key,bytes);
    }
    public static T Read<T>(string path,ReadOnlySpan<byte> key,string purpose)
    {
        path=PhysicalPathGuard.RejectLinks(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length is <1 or >Limit)throw new InvalidDataException("认证文件长度无效。");
        var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);var envelope=StrictJson.Read<Envelope>(new UTF8Encoding(false,true).GetString(bytes),Limit);
        if(envelope.Version!=1||envelope.Body is not {Length:>0 and <=131072}||envelope.Tag is not {Length:32}||!CryptographicOperations.FixedTimeEquals(Tag(key,purpose,envelope.Body),envelope.Tag))throw new InvalidDataException("认证文件内容或用途不符。");
        return StrictJson.Read<T>(new UTF8Encoding(false,true).GetString(envelope.Body),131072);
    }
    public static void Write<T>(string path,ReadOnlySpan<byte> key,string purpose,T value)
    {
        path=PhysicalPathGuard.RejectLinks(path);var body=JsonSerializer.SerializeToUtf8Bytes(value);
        if(body.Length is <1 or >131072)throw new InvalidDataException("认证文件正文超限。");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,body,Tag(key,purpose,body)));if(bytes.Length>Limit)throw new InvalidDataException("认证文件超限。");
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}
            PhysicalPathGuard.RejectLinks(path);File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
