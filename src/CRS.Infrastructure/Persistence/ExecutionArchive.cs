using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Dapper;

namespace CRS.Infrastructure;

/// <summary>把匹配运行封存的三个程序集存入加密库；只校验和归档，绝不动态加载执行旧文件。</summary>
internal static class ExecutionArchive
{
    private const int MaxBytes=16*1024*1024;
    internal static void Save(SqliteConnection c,SqliteTransaction tx,CalculationResult result)
    {
        var seal=result.EvidenceSeal??throw new CrsException("新任务缺少运行封存，不能归档。");
        foreach(var (role,path,expected) in Files(seal))
        {
            using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(source.Length is <=0 or >MaxBytes) throw new CrsException("执行程序文件大小超限，不能保存运行证据。");
            var bytes=new byte[(int)source.Length]; source.ReadExactly(bytes);
            var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            if(!hash.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new CrsException("执行程序已改变，与运行封存不一致，任务未保存。");
            c.Execute("INSERT OR IGNORE INTO execution_artifacts(sha256,assembly_name,byte_length,content) VALUES(@Hash,@Name,@Size,@Content)",
                new {Hash=hash,Name=Path.GetFileNameWithoutExtension(path),Size=bytes.Length,Content=bytes},tx);
            // 已有摘要不能掩盖损坏的去重正文，先核验再关联任务。
            VerifyContent(c,tx,hash,Path.GetFileNameWithoutExtension(path));
            c.Execute("INSERT INTO run_execution_artifacts(snapshot_id,role,sha256) VALUES(@Id,@Role,@Hash)",new {Id=result.SnapshotId,Role=role,Hash=hash},tx);
        }
    }
    internal static void Verify(SqliteConnection c,SqliteTransaction tx,string id,RunEvidenceSeal? seal)
    {
        if(seal is null) throw new CrsException("归档任务缺少运行封存，记录不一致。");
        var links=c.Query<Link>("SELECT role AS Role,sha256 AS Sha256 FROM run_execution_artifacts WHERE snapshot_id=@Id",new {Id=id},tx).ToArray();
        if(links.Length!=3) throw new CrsException("运行程序归档缺失，不能使用该记录。");
        foreach(var (role,path,expected) in Files(seal))
        {
            var link=links.SingleOrDefault(l=>l.Role==role);
            if(link is null || !link.Sha256.Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new CrsException("运行程序归档与封存不一致。");
            VerifyContent(c,tx,link.Sha256,Path.GetFileNameWithoutExtension(path));
        }
    }
    private static void VerifyContent(SqliteConnection c,SqliteTransaction tx,string hash,string name)
    {
        // 在读取 BLOB 之前核验长度，损坏库不能令客户端分配任意大小的正文。
        var size=c.ExecuteScalar<long?>("SELECT length(content) FROM execution_artifacts WHERE sha256=@Hash AND assembly_name=@Name AND byte_length=length(content)",new {Hash=hash,Name=name},tx);
        if(size is null or <=0 or >MaxBytes) throw new CrsException("执行程序归档格式无效。");
        var bytes=c.QuerySingle<byte[]>("SELECT content FROM execution_artifacts WHERE sha256=@Hash",new {Hash=hash},tx);
        if(!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(hash,StringComparison.OrdinalIgnoreCase)) throw new CrsException("执行程序归档摘要不一致，记录可能损坏。");
    }
    private static (string Role,string Path,string Hash)[] Files(RunEvidenceSeal seal)=>
    [ ("Application",typeof(CalculationResult).Assembly.Location,seal.ApplicationSha256),
      ("Domain",typeof(FifoEngine).Assembly.Location,seal.DomainSha256),
      ("Infrastructure",typeof(LocalStore).Assembly.Location,seal.InfrastructureSha256) ];
    private sealed class Link {public string Role {get;set;}=""; public string Sha256 {get;set;}="";}
}
