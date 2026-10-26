using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed record NationalTeamPlaintextExport(string DatabaseKind,int SchemaVersion,int Tables,long Rows,string LogicalSha256);

/// <summary>显式离线导出旧国家队加密库到新文件；不覆盖、替换或删除原库，不由正常启动自动调用。</summary>
public static class LegacyNationalTeamExport
{
    public static NationalTeamPlaintextExport Export(string sourcePath,string destinationPath,byte[] legacyKey)
    {
        var source=Path.GetFullPath(sourcePath);var destination=Path.GetFullPath(destinationPath);
        var kind=Path.GetFileName(source) switch{"market.db"=>"Market","source-policy.db"=>"SourcePolicy",_=>throw new InvalidDataException("只允许迁移国家队市场库或来源策略库。")};
        if(legacyKey is not {Length:32}||string.Equals(source,destination,StringComparison.OrdinalIgnoreCase)
            ||!File.Exists(source)||File.Exists(destination)||Directory.Exists(destination))
            throw new InvalidDataException("迁移密钥、来源或新目标无效；不能覆盖已有文件。");
        using(var file=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        {
            Span<byte> header=stackalloc byte[16];
            if(file.Read(header)==16&&header.SequenceEqual("SQLite format 3\0"u8))throw new InvalidDataException("来源已为普通SQLite，不需要旧加密导出。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging=destination+".preparing-"+Guid.NewGuid().ToString("N");
        var input=staging+".input.db";
        try
        {
            // 独占读取原库及可能尚未检查点的WAL；正在使用的库拒绝迁移，只在副本上恢复/解密。
            using(var original=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.None))
            using(var wal=File.Exists(source+"-wal")?new FileStream(source+"-wal",FileMode.Open,FileAccess.Read,FileShare.None):null)
            {
                using(var copy=new FileStream(input,FileMode.CreateNew,FileAccess.Write,FileShare.None))original.CopyTo(copy);
                if(wal is not null){using var copy=new FileStream(input+"-wal",FileMode.CreateNew,FileAccess.Write,FileShare.None);wal.CopyTo(copy);}
            }
            NationalTeamPlaintextExport expected;
            SQLitePCL.Batteries_V2.Init();
            // 只对工作副本开放创建权限，使ATTACH能新建导出目标；原文件从不以可写连接打开。
            using(var encrypted=new SqliteConnection(new SqliteConnectionStringBuilder
                {DataSource=input,Mode=SqliteOpenMode.ReadWriteCreate,Password=Convert.ToHexString(legacyKey),Pooling=false}.ToString()))
            {
                encrypted.Open();
                var version=Convert.ToInt32(Scalar(encrypted,"PRAGMA user_version"));
                var marker=kind=="Market"?"plugin_schema_migration":"source_policy";
                if(version!=(kind=="Market"?2:1)||Convert.ToInt32(Scalar(encrypted,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name",("$name",marker)))!=1)
                    throw new InvalidDataException("旧库身份或版本不属于国家队数据，拒绝导出。");
                RequireIntegrity(encrypted);
                Execute(encrypted,"ATTACH DATABASE $target AS exported KEY ''",("$target",staging));
                using(var snapshot=encrypted.BeginTransaction(deferred:true))
                {
                    expected=Describe(encrypted,kind,version);
                    _=Scalar(encrypted,"SELECT sqlcipher_export('exported')");
                    Execute(encrypted,"PRAGMA exported.user_version="+version);
                    snapshot.Commit();
                }
                Execute(encrypted,"DETACH DATABASE exported");
            }
            using(var plain=PublicResearchSqlite.Open(staging,true))
            {
                RequireIntegrity(plain);
                using var foreignKeys=plain.CreateCommand();foreignKeys.CommandText="PRAGMA foreign_key_check";
                using var reader=foreignKeys.ExecuteReader();if(reader.Read())throw new InvalidDataException("导出资料存在外键不一致，拒绝发布目标。");
                reader.Close();
                var actual=Describe(plain,kind,Convert.ToInt32(Scalar(plain,"PRAGMA user_version")));
                if(actual!=expected)throw new InvalidDataException("导出前后结构或逐行内容不一致，拒绝发布目标。");
            }
            File.Move(staging,destination);
            return expected;
        }
        finally
        {
            // 仅清理本次新建的暂存目标，原文件与调用方的现有目标始终保留。
            if(File.Exists(staging))File.Delete(staging);
            foreach(var file in new[]{input,input+"-wal",input+"-shm"})if(File.Exists(file))File.Delete(file);
        }
    }
    private static void RequireIntegrity(SqliteConnection db)
    {if(Scalar(db,"PRAGMA integrity_check") as string!="ok")throw new InvalidDataException("迁移资料完整性校验失败。");}
    private static NationalTeamPlaintextExport Describe(SqliteConnection db,string kind,int version)
    {
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(object value)=>digest.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)+"\n"));
        var tables=new List<string>();
        using(var command=db.CreateCommand())
        {
            command.CommandText="SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name";
            using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                Add(Enumerable.Range(0,4).Select(i=>reader.IsDBNull(i)?null:reader.GetString(i)).ToArray());
                if(reader.GetString(0)=="table")tables.Add(reader.GetString(1));
            }
        }
        long rows=0;
        foreach(var table in tables.Order(StringComparer.Ordinal))
        {
            var columns=new List<string>();using(var command=db.CreateCommand())
            {command.CommandText="PRAGMA table_info("+Quote(table)+")";using var reader=command.ExecuteReader();while(reader.Read())columns.Add(Quote(reader.GetString(1)));}
            var values=columns.Select(c=>"quote("+c+")").ToArray();
            using var query=db.CreateCommand();query.CommandText="SELECT "+string.Join(',',values)+" FROM "+Quote(table)+" ORDER BY "+string.Join(',',values.Select(c=>c+" COLLATE BINARY"));
            using var data=query.ExecuteReader();Add(table);
            while(data.Read()){Add(Enumerable.Range(0,columns.Count).Select(data.GetString).ToArray());rows++;}
        }
        return new(kind,version,tables.Count,rows,Convert.ToHexString(digest.GetHashAndReset()));
    }
    private static string Quote(string identifier)=>"\""+identifier.Replace("\"","\"\"")+"\"";
    private static object? Scalar(SqliteConnection db,string sql,params (string,object)[] args)
    {using var command=db.CreateCommand();command.CommandText=sql;foreach(var (name,value) in args)command.Parameters.AddWithValue(name,value);return command.ExecuteScalar();}
    private static void Execute(SqliteConnection db,string sql,params (string,object)[] args)
    {using var command=db.CreateCommand();command.CommandText=sql;foreach(var (name,value) in args)command.Parameters.AddWithValue(name,value);command.ExecuteNonQuery();}
}
