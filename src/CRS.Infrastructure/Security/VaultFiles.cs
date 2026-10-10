using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using CRS.Security.Recovery;

namespace CRS.Infrastructure;

/// <summary>保险库格式和原子文件提交；不读取或迁移任何明文 SQLite 业务记录。</summary>
internal static class VaultFiles
{
    internal static readonly JsonSerializerOptions Options=new() { RespectRequiredConstructorParameters=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    internal static string EnvelopePath(string directory)=>Path.Combine(directory,"vault.json");
    internal static void RejectPlaintext(string database)
    {
        // 只读取头部；允许 SQLCipher 已有的 WAL 连接持有文件，不影响加密数据库的一致性读取。
        using var stream=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); Span<byte> header=stackalloc byte[16];
        if (stream.Read(header)==16 && header.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")))
            throw new CrsException("检测到未加密的旧数据库，本版本拒绝读取和转换。请选择新的空目录创建加密保险库，旧文件不会被修改。");
        if (stream.Length<4096 || stream.Length%4096!=0) throw new CrsException("数据库格式未知或已损坏，不会自动重建或覆盖。");
    }
    internal static VaultEnvelope ReadEnvelope(string directory)
    {
        using var stream=File.OpenRead(EnvelopePath(directory));
        if (stream.Length is <=0 or >65536) throw new CrsException("保险库元数据大小无效。");
        try
        {
            var envelope=JsonSerializer.Deserialize<VaultEnvelope>(stream,Options)??throw new JsonException();
            if(envelope.Version!=1) throw new CrsException("保险库版本不受支持，不会自动降级或重建。");
            return envelope;
        }
        catch(JsonException) { throw new CrsException("保险库元数据损坏或版本不支持。"); }
    }
    internal static void AtomicWrite(string path,byte[] data,bool overwrite=false)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(data); stream.Flush(true); }
            File.Move(temporary,path,overwrite);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static string BeginNewDirectory(string target)
    {
        var full=Path.GetFullPath(target);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() || File.Exists(full)) throw new CrsException("创建或恢复只允许新的空目录，现有文件不会被覆盖。");
        var parent=Path.GetDirectoryName(full)??throw new CrsException("不能在文件系统根目录创建保险库。"); Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,".crs-new-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging); return staging;
    }
    internal static void CommitDirectory(string staging,string target)
    {
        var full=Path.GetFullPath(target);
        if (Directory.Exists(full)) { if (Directory.EnumerateFileSystemEntries(full).Any()) throw new CrsException("目标目录已发生变化，未覆盖任何文件。"); Directory.Delete(full); }
        Directory.Move(staging,full);
    }
}
