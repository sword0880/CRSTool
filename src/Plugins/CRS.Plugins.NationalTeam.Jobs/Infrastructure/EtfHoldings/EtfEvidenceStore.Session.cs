using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>ETF原件、可比口径与具名持仓拥有独立存储；沿用共享会话与同事务待评价标记。</summary>
internal sealed partial class EtfEvidenceStore(IMarketRepositorySession session,EtfStore etfs,Action markPending)
{
    private SqliteConnection db=>session.Connection;
    private RawEvidenceStore raw=>session.RawEvidence;
    private Action? beforeWrite=>session.BeforeWrite;
    private TimeProvider clock=>session.Clock;
    private string Now=>session.Now;
    private SqliteCommand Command(string sql,params (string,object?)[] args)=>session.Command(sql,args);
    private void Execute(string sql,params (string,object?)[] args)=>session.Execute(sql,args);
    private object? Scalar(string sql,params (string,object?)[] args)=>session.Scalar(sql,args);
    private bool Committed(Guid? id)=>session.Committed(id);
    private void RecordCommand(Guid? id,string operation)=>session.RecordCommand(id,operation);
    private void MarkAlertPending()=>markPending();
    private List<EtfConfiguration> Configuration()=>etfs.Configuration();
    private string RecordFetch(string hash,string url,string originalHash,string provider,string parser)
        =>session.RecordFetch(hash,url,originalHash,provider,parser);
    private static List<T> ReadJson<T>(SqliteCommand command)
    {
        using var reader=command.ExecuteReader();var result=new List<T>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new InvalidDataException("ETF证据记录损坏。"));
        return result;
    }
    public void InitializeSchema()
    {
        using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS nt_alert_document(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,url TEXT NOT NULL,hash TEXT NOT NULL,first_seen TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id));
            CREATE TABLE IF NOT EXISTS nt_alert_metadata(market TEXT NOT NULL,ticker TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(market,ticker));
            CREATE TABLE IF NOT EXISTS nt_alert_metadata_review(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,document_id TEXT NOT NULL,at TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_named_etf_holding(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,payload TEXT NOT NULL);
            """);
        transaction.Commit();
    }
    /// <summary>预警冻结历史只取得原件的来源身份，不拥有或直接查询ETF原件表。</summary>
    internal IReadOnlyList<string> FetchIdsForHash(string hash)
    {
        using var command=Command("SELECT DISTINCT fetch_id FROM nt_alert_document WHERE hash=$h",("$h",hash));
        using var reader=command.ExecuteReader();var result=new List<string>();
        while(reader.Read()){session.OperationCancellation.ThrowIfCancellationRequested();result.Add(reader.GetString(0));}
        return result;
    }
}
