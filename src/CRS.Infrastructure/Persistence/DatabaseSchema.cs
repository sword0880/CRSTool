using Microsoft.Data.Sqlite;
using Dapper;
using CRS.Security.Vault;

namespace CRS.Infrastructure;

/// <summary>只升级已验证的加密业务库；结构修改与版本提交同事务，旧结果不重算、不重封存。</summary>
internal static class DatabaseSchema
{
    internal const int CurrentVersion=2;
    private const string BaseTables="""
        CREATE TABLE calculations(id TEXT PRIMARY KEY, year INTEGER NOT NULL, created_utc TEXT NOT NULL,
          complete INTEGER NOT NULL, issues INTEGER NOT NULL, result_json TEXT NOT NULL, carry BLOB,
          execution_archived INTEGER NOT NULL DEFAULT 0 CHECK(execution_archived IN (0,1)));
        CREATE TABLE reviews(id INTEGER PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES calculations(id),
          category TEXT NOT NULL, evidence TEXT NOT NULL, created_utc TEXT NOT NULL);
        """;
    private const string ArchiveTables="""
        CREATE TABLE execution_artifacts(sha256 TEXT PRIMARY KEY, assembly_name TEXT NOT NULL,
          byte_length INTEGER NOT NULL CHECK(byte_length>0 AND byte_length<=16777216),
          content BLOB NOT NULL CHECK(length(content)=byte_length));
        CREATE TABLE run_execution_artifacts(snapshot_id TEXT NOT NULL REFERENCES calculations(id),
          role TEXT NOT NULL CHECK(role IN ('Application','Domain','Infrastructure')),
          sha256 TEXT NOT NULL REFERENCES execution_artifacts(sha256),PRIMARY KEY(snapshot_id,role));
        """;

    // 故障回调按单次调用注入，仅内部测试使用，生产路径没有全局可变开关。
    internal static void Ensure(VaultSession session,SqliteConnection c,bool create=false,Action? afterChanges=null,
        Action<VaultSession,VaultEnvelope,string>? writeBackup=null)
    {
        using var tx=c.BeginTransaction();
        var version=c.ExecuteScalar<int>("PRAGMA user_version",transaction:tx);
        if(version>CurrentVersion) throw new CrsException("数据库版本高于当前程序，请使用较新版本。");
        if(create)
        {
            if(version!=0 || c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table'",transaction:tx)!=0)
                throw new CrsException("新建目标已有数据库结构，不会重建或覆盖。");
            c.Execute(BaseTables+ArchiveTables+"PRAGMA user_version=2;",transaction:tx);
        }
        else
        {
            if(version is not (1 or CurrentVersion)) throw new CrsException("保险库业务格式无效或版本不支持。");
            Validate(c,tx,version);
            if(version==1)
            {
                // 先保留同格式可恢复备份；备份失败时还未执行任何业务结构修改。
                var path=Path.Combine(session.DirectoryPath,"schema-v1-"+Guid.NewGuid().ToString("N")+".crsbak");
                (writeBackup??VaultBackupFiles.Write)(session,VaultFiles.ReadEnvelope(session.DirectoryPath),path);
                c.Execute("ALTER TABLE calculations ADD COLUMN execution_archived INTEGER NOT NULL DEFAULT 0 CHECK(execution_archived IN (0,1));"+ArchiveTables+"PRAGMA user_version=2;",transaction:tx);
            }
        }
        Validate(c,tx,CurrentVersion); afterChanges?.Invoke();
        if(c.Query<string>("PRAGMA foreign_key_check",transaction:tx).Any() || c.ExecuteScalar<string>("PRAGMA integrity_check",transaction:tx)!="ok")
            throw new CrsException("数据库升级后的完整性检查失败，结构修改已取消。");
        tx.Commit();
    }

    private static void Validate(SqliteConnection c,SqliteTransaction tx,int version)
    {
        // 比较实际列而非仅信任 user_version；未知结构不能被当作旧版自动修补。
        Columns("calculations",new[]{("id","TEXT",0L,1L),("year","INTEGER",1L,0L),("created_utc","TEXT",1L,0L),
            ("complete","INTEGER",1L,0L),("issues","INTEGER",1L,0L),("result_json","TEXT",1L,0L),("carry","BLOB",0L,0L)}
            .Concat(version==2?new[]{("execution_archived","INTEGER",1L,0L)}:[]).ToArray());
        Columns("reviews",[("id","INTEGER",0,1),("snapshot_id","TEXT",1,0),("category","TEXT",1,0),("evidence","TEXT",1,0),("created_utc","TEXT",1,0)]);
        References("reviews",[("snapshot_id","calculations","id")]);
        if(version==2)
        {
            Columns("execution_artifacts",[("sha256","TEXT",0,1),("assembly_name","TEXT",1,0),("byte_length","INTEGER",1,0),("content","BLOB",1,0)]);
            Columns("run_execution_artifacts",[("snapshot_id","TEXT",1,1),("role","TEXT",1,2),("sha256","TEXT",1,0)]);
            References("run_execution_artifacts",[("snapshot_id","calculations","id"),("sha256","execution_artifacts","sha256")]);
        }
        void Columns(string table,(string Name,string Type,long Required,long PrimaryKey)[] expected)
        {
            var actual=c.Query<Column>("SELECT name AS Name,type AS Type,\"notnull\" AS Required,pk AS PrimaryKey FROM pragma_table_info(@Table)",new {Table=table},tx).ToArray();
            if(actual.Length!=expected.Length || expected.Any(e=>!actual.Any(a=>a.Name==e.Name && a.Type.Equals(e.Type,StringComparison.OrdinalIgnoreCase) && a.Required==e.Required && a.PrimaryKey==e.PrimaryKey)))
                throw new CrsException("保险库业务表结构无效，不会自动重建或覆盖。");
        }
        void References(string table,(string Source,string Target,string Column)[] expected)
        {
            var actual=c.Query<Reference>("SELECT \"from\" AS Source,\"table\" AS Target,\"to\" AS TargetColumn FROM pragma_foreign_key_list(@Table)",new {Table=table},tx).ToArray();
            if(actual.Length!=expected.Length || expected.Any(e=>!actual.Any(a=>a.Source==e.Source && a.Target==e.Target && a.TargetColumn==e.Column)))
                throw new CrsException("保险库业务关联结构无效，不会自动修补。");
        }
    }
    private sealed class Column
    {
        public string Name {get;set;}=""; public string Type {get;set;}="";
        public long Required {get;set;} public long PrimaryKey {get;set;}
    }
    private sealed class Reference {public string Source {get;set;}=""; public string Target {get;set;}=""; public string TargetColumn {get;set;}="";}
}
