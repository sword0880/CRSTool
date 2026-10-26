using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>独立拥有公开研究库连接、只读快照及取消作用域；不引用聚合仓储或业务页面。</summary>
internal sealed class MarketDatabaseSession : IMarketRepositorySession,IDisposable
{
    private readonly string path;
    private readonly SourceFetchStore fetches;
    private EvidenceIntegrityStore? integrity;
    private int disposed;
    public SqliteConnection Connection {get;}
    public RawEvidenceStore RawEvidence {get;}
    public Action? BeforeWrite {get;}
    public TimeProvider Clock {get;}
    public CancellationToken OperationCancellation {get;private set;}
    public bool IsReadSnapshot {get;private set;}
    public string Now=>CRS.Timing.BeijingClock.Read(Clock).ToString("O");
    private MarketDatabaseSession(string path,RawEvidenceStore raw,Action? beforeWrite,TimeProvider clock,bool readOnly)
    {
        this.path=path;RawEvidence=raw;Clock=clock;
        BeforeWrite=readOnly?()=>throw new InvalidOperationException("只读查询不允许写入。"):beforeWrite;
        Connection=PublicResearchSqlite.Open(path,readOnly);
        fetches=new(this);
        try{Execute(readOnly?"PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"
            :"PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");}
        catch{Connection.Dispose();throw;}
    }
    public static MarketDatabaseSession OpenWriter(string directory,RawEvidenceStore raw,Action? beforeWrite,TimeProvider clock)
    {Directory.CreateDirectory(directory);return new(Path.Combine(directory,"market.db"),raw,beforeWrite,clock,false);}
    public MarketDatabaseSession OpenReader()
    {ThrowIfDisposed();return new(path,RawEvidence,null,Clock,true);}
    public void AttachIntegrity(EvidenceIntegrityStore value)
    {
        if(integrity is not null)throw new InvalidOperationException("完整性模块不可重复装配。");
        integrity=value??throw new ArgumentNullException(nameof(value));
    }
    public IReadOnlyDictionary<string,bool> ValidateEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
        =>(integrity??throw new InvalidOperationException("完整性模块尚未装配。")).Validate(shareKey,includeCalendar,activityOnly,dashboardOnly,includeAttempts);
    public SqliteCommand Command(string sql,params (string,object?)[] args)
    {
        ThrowIfDisposed();OperationCancellation.ThrowIfCancellationRequested();
        var command=Connection.CreateCommand();command.CommandText=sql;
        foreach(var (name,value) in args)command.Parameters.AddWithValue(name,value??DBNull.Value);
        return command;
    }
    public void Execute(string sql,params (string,object?)[] args)
    {using var command=Command(sql,args);command.ExecuteNonQuery();}
    public object? Scalar(string sql,params (string,object?)[] args)
    {using var command=Command(sql,args);return command.ExecuteScalar();}
    public bool Committed(Guid? id)=>id is {} value&&Convert.ToInt64(Scalar("SELECT count(*) FROM worker_business_command WHERE request=$r",("$r",value.ToString("N"))))>0;
    public void RecordCommand(Guid? id,string operation)
    {if(id is {} value)Execute("INSERT INTO worker_business_command VALUES($r,$o,$now)",("$r",value.ToString("N")),("$o",operation),("$now",Now));}
    public string RecordFetch(string hash,string sourceUrl,string? originalHash=null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1")
        =>fetches.RecordFetch(hash,sourceUrl,originalHash,provider,parserVersion);
    public void MarkFetchFailed(string fetchId)=>fetches.MarkFailed(fetchId);
    public void CheckReadLimit(int count)
    {
        if(IsReadSnapshot&&count>10000)throw new InvalidDataException("查询依赖集合超过10000项，请缩小查询范围。");
        OperationCancellation.ThrowIfCancellationRequested();
    }
    public T ExecuteOperation<T>(Func<T> work,CancellationToken token)
    {
        ThrowIfDisposed();token.ThrowIfCancellationRequested();
        var previous=OperationCancellation;OperationCancellation=token;
        using var interrupt=token.Register(()=>SQLitePCL.raw.sqlite3_interrupt(Connection.Handle));
        try{token.ThrowIfCancellationRequested();var result=work();token.ThrowIfCancellationRequested();return result;}
        catch(SqliteException error) when(error.SqliteErrorCode==9&&token.IsCancellationRequested)
        {throw new OperationCanceledException("市场查询已取消。",error,token);}
        catch(SqliteException error){throw new IOException("插件市场仓储操作失败。",error);}
        finally{OperationCancellation=previous;}
    }
    public T ReadSnapshot<T>(Func<T> query,CancellationToken token)=>ExecuteOperation(()=>
    {
        using var transaction=Connection.BeginTransaction(deferred:true);IsReadSnapshot=true;
        try
        {
            _=Scalar("SELECT count(*) FROM plugin_schema_migration");
            var result=query();token.ThrowIfCancellationRequested();transaction.Commit();return result;
        }
        finally{IsReadSnapshot=false;}
    },token);
    public void Snapshot(string target)
    {
        ThrowIfDisposed();using var destination=PublicResearchSqlite.Open(target);
        Connection.BackupDatabase(destination);
        using var check=destination.CreateCommand();check.CommandText="PRAGMA integrity_check";
        if(check.ExecuteScalar() as string!="ok")throw new InvalidDataException("Snapshot integrity failure.");
    }
    private void ThrowIfDisposed()=>ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)Connection.Dispose();}
}
