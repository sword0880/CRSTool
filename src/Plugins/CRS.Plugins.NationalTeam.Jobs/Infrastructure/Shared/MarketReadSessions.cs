using CRS.Plugins.NationalTeamWatch.Application;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository : IMarketReadSessions,IBoundedMarketEvidence
{
    private string readConnectionString = "";
    private bool readSnapshot;

    private MarketRepository(string connectionString,RawEvidenceStore evidence,TimeProvider timeProvider)
    {
        raw=evidence;clock=timeProvider;
        beforeWrite=()=>throw new InvalidOperationException("只读查询不允许写入。");
        var password = new SqliteConnectionStringBuilder(connectionString).Password;
        byte[]? databaseKey = null;
        try
        {
            databaseKey = Convert.FromHexString(password);
            if (databaseKey.Length == 32)
                marketCycleApprovalAuditKey = DeriveMarketCycleApprovalAuditKey(databaseKey);
        }
        catch (FormatException) { /* 非标准只读连接不获得审批注册表验证能力。 */ }
        finally
        {
            if (databaseKey is not null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(databaseKey);
        }
        db=new(connectionString);
        marketSession=new MarketRepositorySession(this);
        evidenceIntegrityStore=new(marketSession,MarkAlertPending,RecordQueryStage);
        industryPeStore=new IndustryPeStore(marketSession);
        equityStore=new EquityStore(marketSession);
        marketCalendarStore=new MarketCalendarStore(marketSession);
        indexSeriesStore=new IndexSeriesStore(marketSession);
        marketActivityStore=new MarketActivityStore(marketSession,marketCalendarStore);
        navStore=new NavStore(marketSession);
        alertStore=new AlertStore(marketSession,new AlertMarketFacts(this));
        try
        {
            db.Open();
            Execute("PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        }
        catch{db.Dispose();throw;}
    }

    public T Read<T>(Func<IMarketRepository,T> query,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var verificationPriority=raw.PrioritizeQueryVerification();
        using var reader=new MarketRepository(readConnectionString,raw,clock);
        return reader.ExecuteOperation(()=>
        {
            using var transaction=reader.db.BeginTransaction(deferred:true);
            reader.readSnapshot=true;
            // 建立快照后才核验原件；同次读取的事实、修订和质量状态属于同一代。
            _=reader.Scalar("SELECT count(*) FROM plugin_schema_migration");
            var result=query(reader);token.ThrowIfCancellationRequested();transaction.Commit();return result;
        },token);
    }

    private void CheckReadLimit(int count)
    {
        if(readSnapshot&&count>10000)throw new InvalidDataException("查询依赖集合超过10000项，请缩小查询范围。");
        operationCancellation.ThrowIfCancellationRequested();
    }

    public List<EvidenceRow> Evidence(EtfKey? key,DateOnly? from,DateOnly? to,int maximumRows)
    {
        if(maximumRows is <1 or >10000||from>to)throw new ArgumentException("证据查询范围或数量无效。");
        // 多读一行只用于判断超限；先拒绝超量结果，再读取原件和序列化，避免先构造整库快照。
        var rows=ReadEvidence(key,from,to,maximumRows+1);
        if(rows.Count>maximumRows)throw new InvalidDataException("证据结果超过限额，请缩小证券或日期范围。");
        var verified=raw.VerifyMany(rows.SelectMany(x=>new[]{x.Hash,x.OriginalHash}),operationCancellation);
        if(verified.Values.Any(x=>!x))throw new InvalidDataException("Evidence is missing or damaged.");
        return rows;
    }
}
