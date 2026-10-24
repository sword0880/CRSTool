using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CRS.Worker.Service;

/// <summary>灾备授权的加密持久索引；调用方须持数据租约、核验 ACL，不能拿诊断日志代替审计。</summary>
internal sealed class RecoveryExportJournal : IDisposable
{
    internal const int MaximumAttempts = 1024;
    private readonly FileStream lease;
    private SqliteConnection db = null!;

    internal RecoveryExportJournal(string directory, ReadOnlySpan<byte> serviceKey)
    {
        if(serviceKey.Length!=32)throw new ArgumentException("服务密钥长度错误。");
        var path=Path.Combine(directory,"recovery-authorizations.db");
        var lockPath=Path.Combine(directory,"recovery-authorizations.lock");
        var marker=Path.Combine(directory,"recovery-authorizations.initialized");
        StorageSecurity.RejectLinks(path);StorageSecurity.RejectLinks(lockPath);StorageSecurity.RejectLinks(marker);
        lease=new(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var existed=File.Exists(path);
        Span<byte> key=stackalloc byte[32];
        try
        {
            // 索引丢失不能被当作首次使用，否则旧确认请求会失去持久重放保护。
            if(existed!=File.Exists(marker))throw new InvalidDataException("恢复授权索引或初始化标记缺失。");
            if(!existed)
            {
                using var initialized=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                initialized.Write("CRS.Worker.RecoveryAudit/v1"u8);initialized.Flush(true);
            }
            SQLitePCL.Batteries_V2.Init();
            HKDF.DeriveKey(HashAlgorithmName.SHA256,serviceKey,key,ReadOnlySpan<byte>.Empty,"CRS.Worker.RecoveryAudit/v1"u8);
            db=new(new SqliteConnectionStringBuilder{DataSource=path,Password=Convert.ToHexString(key),Pooling=false,DefaultTimeout=3}.ToString());
            db.Open();Initialize(existed);
            // 新进程没有旧的内存挑战和单调期限，遗留会话只可作废，不能恢复成可确认状态。
            using var transaction=db.BeginTransaction();
            Execute("""
                INSERT INTO recovery_audit(prepare,request,phase,reason,time)
                SELECT prepare,'00000000000000000000000000000000','Abandoned','ProcessRestart',$time
                FROM recovery_attempt WHERE state IN ('Prepared','Consuming','Authorized');
                UPDATE recovery_attempt SET state='Abandoned' WHERE state IN ('Prepared','Consuming','Authorized');
                """,transaction,("$time",CRS.Timing.BeijingClock.Serialize(CRS.Timing.BeijingClock.Now)));
            transaction.Commit();
        }
        catch{db?.Dispose();lease.Dispose();throw;}
        finally{CryptographicOperations.ZeroMemory(key);}
    }

    private void Initialize(bool existed)
    {
        var version=Convert.ToInt32(Scalar("PRAGMA user_version"));
        if(existed&&version!=1||!existed&&version!=0)throw new InvalidDataException("恢复审计库版本不受支持。");
        Execute("PRAGMA busy_timeout=3000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        if(existed)
        {
            if(Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('recovery_attempt','recovery_request','recovery_audit')"))!=3||
                !string.Equals(Scalar("PRAGMA quick_check") as string,"ok",StringComparison.Ordinal))
                throw new InvalidDataException("恢复审计库不完整。");
            return;
        }
        Execute("""
            CREATE TABLE recovery_attempt(prepare TEXT PRIMARY KEY,bundle TEXT NOT NULL UNIQUE,sid TEXT NOT NULL,digest BLOB NOT NULL,state TEXT NOT NULL);
            CREATE TABLE recovery_request(id TEXT PRIMARY KEY,prepare TEXT NOT NULL REFERENCES recovery_attempt(prepare));
            CREATE TABLE recovery_audit(seq INTEGER PRIMARY KEY,prepare TEXT NOT NULL REFERENCES recovery_attempt(prepare),request TEXT NOT NULL,phase TEXT NOT NULL,reason TEXT NOT NULL,time TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
    }

    internal void Reserve(string sid,Guid prepare,Guid bundle,ReadOnlySpan<byte> digest)
    {
        using var transaction=db.BeginTransaction();
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM recovery_attempt",transaction))>=MaximumAttempts)
            throw Refused(RecoveryExportFailure.CapacityExceeded);
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM recovery_attempt WHERE state IN ('Prepared','Consuming','Authorized')",transaction))>=4||
            Convert.ToInt32(Scalar("SELECT count(*) FROM recovery_attempt WHERE sid=$sid AND state IN ('Prepared','Consuming','Authorized')",transaction,("$sid",sid)))>=1)
            throw Refused(RecoveryExportFailure.CapacityExceeded);
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM recovery_request WHERE id=$id",transaction,("$id",prepare.ToString("N"))))!=0||
            Convert.ToInt32(Scalar("SELECT count(*) FROM recovery_attempt WHERE bundle=$bundle",transaction,("$bundle",bundle.ToString("N"))))!=0)
            throw Refused(RecoveryExportFailure.Consumed);
        Execute("INSERT INTO recovery_attempt VALUES($prepare,$bundle,$sid,$digest,'Prepared')",transaction,
            ("$prepare",prepare.ToString("N")),("$bundle",bundle.ToString("N")),("$sid",sid),("$digest",digest.ToArray()));
        Execute("INSERT INTO recovery_request VALUES($id,$prepare)",transaction,("$id",prepare.ToString("N")),("$prepare",prepare.ToString("N")));
        Audit(prepare,prepare,"Prepared","",transaction);transaction.Commit();
    }

    internal bool Consume(Guid prepare,Guid confirmation)
    {
        using var transaction=db.BeginTransaction();
        // 先消费准备记录；碰撞、空编号和跨操作重放也不能恢复为可用挑战。
        if(Execute("UPDATE recovery_attempt SET state='Consuming' WHERE prepare=$prepare AND state='Prepared'",transaction,("$prepare",prepare.ToString("N")))!=1)
            return false;
        var accepted=confirmation!=Guid.Empty&&Execute("INSERT OR IGNORE INTO recovery_request VALUES($id,$prepare)",transaction,
            ("$id",confirmation.ToString("N")),("$prepare",prepare.ToString("N")))==1;
        if(!accepted)Execute("UPDATE recovery_attempt SET state='Rejected' WHERE prepare=$prepare",transaction,("$prepare",prepare.ToString("N")));
        Audit(prepare,confirmation,accepted?"Consumed":"Rejected",accepted?"":"RequestReplay",transaction);
        transaction.Commit();return accepted;
    }

    internal void Record(RecoveryExportAudit audit)
    {
        if(audit.Phase==RecoveryExportPhase.Prepared)return; // Reserve 已将准备与索引在同一事务中落盘。
        using var transaction=db.BeginTransaction();
        var expected=audit.Phase==RecoveryExportPhase.Authorized?"Consuming":"Authorized";
        if(Execute("UPDATE recovery_attempt SET state=$state WHERE prepare=$prepare AND state=$expected",transaction,
            ("$state",audit.Phase.ToString()),("$prepare",audit.PrepareRequestId.ToString("N")),("$expected",expected))!=1)
            throw new InvalidDataException("恢复审计状态转换无效。");
        Audit(audit.PrepareRequestId,audit.RequestId,audit.Phase.ToString(),"",transaction);transaction.Commit();
    }

    internal void Reject(Guid prepare,Guid request,RecoveryExportFailure reason)
    {
        using var transaction=db.BeginTransaction();
        Execute("UPDATE recovery_attempt SET state='Rejected' WHERE prepare=$prepare",transaction,("$prepare",prepare.ToString("N")));
        Audit(prepare,request,"Rejected",reason.ToString(),transaction);transaction.Commit();
    }

    internal string? State(Guid prepare)=>Scalar("SELECT state FROM recovery_attempt WHERE prepare=$prepare",null,("$prepare",prepare.ToString("N"))) as string;
    private void Audit(Guid prepare,Guid request,string phase,string reason,SqliteTransaction transaction)
        =>Execute("INSERT INTO recovery_audit(prepare,request,phase,reason,time) VALUES($prepare,$request,$phase,$reason,$time)",transaction,
            ("$prepare",prepare.ToString("N")),("$request",request.ToString("N")),("$phase",phase),("$reason",reason),("$time",CRS.Timing.BeijingClock.Serialize(CRS.Timing.BeijingClock.Now)));
    private object? Scalar(string sql,SqliteTransaction? transaction=null,params (string Name,object Value)[] values)
    {using var command=Command(sql,transaction,values);return command.ExecuteScalar();}
    private int Execute(string sql,SqliteTransaction? transaction=null,params (string Name,object Value)[] values)
    {using var command=Command(sql,transaction,values);return command.ExecuteNonQuery();}
    private SqliteCommand Command(string sql,SqliteTransaction? transaction,(string Name,object Value)[] values)
    {
        var command=db.CreateCommand();command.CommandText=sql;command.Transaction=transaction;
        foreach(var value in values)command.Parameters.AddWithValue(value.Name,value.Value);
        return command;
    }
    private static RecoveryExportRefusedException Refused(RecoveryExportFailure code)=>new(code);
    public void Dispose(){try{db.Dispose();}finally{lease.Dispose();}}
}
