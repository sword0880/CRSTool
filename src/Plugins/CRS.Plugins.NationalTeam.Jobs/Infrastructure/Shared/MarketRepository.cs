using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>功能仓储组合入口；公开研究资料使用普通SQLite，审批认证密钥不用于数据库加密。</summary>
public sealed partial class MarketRepository : IMarketRepository,IEtfDashboardRepository,IEvidenceIntegrityRepository
{
    private readonly MarketDatabaseSession database;
    private readonly NationalTeamStores stores;
    private SqliteConnection db=>database.Connection;
    private RawEvidenceStore raw=>database.RawEvidence;
    private Action? beforeWrite=>database.BeforeWrite;
    private TimeProvider clock=>database.Clock;
    private CancellationToken operationCancellation=>database.OperationCancellation;
    private string Now=>database.Now;
    private IndustryPeStore industryPeStore=>stores.Industry;
    private MarketCalendarStore marketCalendarStore=>stores.Calendar;
    private IndexSeriesStore indexSeriesStore=>stores.Index;
    private MarketActivityStore marketActivityStore=>stores.Activity;
    private NavStore navStore=>stores.Nav;
    private AlertStore alertStore=>stores.Alerts;
    private EquityStore equityStore=>stores.Equity;
    private EtfStore etfStore=>stores.Etf;
    private EvidenceIntegrityStore evidenceIntegrityStore=>stores.Integrity;
    public MarketRepository(string dataPath,RawEvidenceStore rawStore,Action? beforeWrite=null,byte[]? approvalAuditKey=null,TimeProvider? timeProvider=null,IMarketCycleApprovalWriteAuthority? approvalWriteAuthority=null)
    {
        if(approvalAuditKey is not {Length:32})throw new InvalidDataException("审批审计认证需要独立的32字节密钥；该密钥不用于加密国家队资料库。");
        marketCycleApprovalWriteAuthority=approvalWriteAuthority;
        database=MarketDatabaseSession.OpenWriter(dataPath,rawStore,beforeWrite,timeProvider??TimeProvider.System);
        try
        {
            marketCycleApprovalAuditKey=DeriveMarketCycleApprovalAuditKey(approvalAuditKey);
            stores=new(database);
            NationalTeamSchemaInitializer.Initialize(database,stores,InitializeMarketCycleApprovalSchema);
        }
        catch{System.Security.Cryptography.CryptographicOperations.ZeroMemory(marketCycleApprovalAuditKey);database.Dispose();throw;}
    }
    private MarketRepository(MarketDatabaseSession session,byte[] approvalAuditKey)
    {
        database=session;marketCycleApprovalAuditKey=approvalAuditKey;
        try{stores=new(database);}
        catch{System.Security.Cryptography.CryptographicOperations.ZeroMemory(marketCycleApprovalAuditKey);database.Dispose();throw;}
    }
    private SqliteCommand Command(string sql,params (string,object?)[] args)=>database.Command(sql,args);
    private void Execute(string sql,params (string,object?)[] args)=>database.Execute(sql,args);
    private object? Scalar(string sql,params (string,object?)[] args)=>database.Scalar(sql,args);
    private bool Committed(Guid? id)=>database.Committed(id);
    private void RecordCommand(Guid? id,string operation)=>database.RecordCommand(id,operation);
    private void CheckReadLimit(int count)=>database.CheckReadLimit(count);
    public T ExecuteOperation<T>(Func<T> work,CancellationToken token)=>database.ExecuteOperation(work,token);
    public void Snapshot(string target)=>database.Snapshot(target);
    public void Dispose(){System.Security.Cryptography.CryptographicOperations.ZeroMemory(marketCycleApprovalAuditKey);database.Dispose();}
}
