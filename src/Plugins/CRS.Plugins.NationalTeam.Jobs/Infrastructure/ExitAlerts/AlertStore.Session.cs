using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>预警模块独立装配事件、Outbox及投递Store；跨模块读取只经过窄事实端口。</summary>
internal sealed partial class AlertStore
{
    private readonly IMarketRepositorySession session;
    private readonly IAlertMarketFacts facts;
    private readonly AlertPendingStore pending;
    private readonly AlertOutboxStore alertOutboxStore;
    private readonly AlertEventStore alertEventStore;
    private readonly AlertDeliveryStore alertDeliveryStore;

    public AlertStore(IMarketRepositorySession session, IAlertMarketFacts facts,AlertPendingStore pending)
    {
        this.session = session;
        this.facts = facts;
        this.pending=pending;
        alertOutboxStore = new(session);
        alertEventStore = new(session);
        alertDeliveryStore = new(session, alertOutboxStore);
    }

    private SqliteConnection db => session.Connection;
    private RawEvidenceStore raw => session.RawEvidence;
    private Action? beforeWrite => session.BeforeWrite;
    private TimeProvider clock => session.Clock;
    private string Now => session.Now;
    private SqliteCommand Command(string sql, params (string, object?)[] args) => session.Command(sql, args);
    private void Execute(string sql, params (string, object?)[] args) => session.Execute(sql, args);
    private object? Scalar(string sql, params (string, object?)[] args) => session.Scalar(sql, args);
    private bool Committed(Guid? id) => session.Committed(id);
    private void RecordCommand(Guid? id, string operation) => session.RecordCommand(id, operation);
    private void ValidateEvidence() => session.ValidateEvidence();
    private string RecordFetch(string hash, string sourceUrl, string? originalHash = null,
        string provider = "ReviewedNormalizedManualImport", string parserVersion = "normalized-csv-v1")
        => session.RecordFetch(hash, sourceUrl, originalHash, provider, parserVersion);

    // 保留原调用时机和同一事务快照，不使用公开Points/Evidence入口触发第二次核验或读取。
    private List<EtfAcceptance> Acceptance(DateOnly through) => facts.Acceptance(through);
    private List<EtfConfiguration> Configuration() => facts.Configuration();
    private List<EvidenceRow> ReadEvidence() => facts.Evidence();
    private List<(DateOnly Date, bool Valid)> ReadCalendar(string market) => facts.Calendar(market);
    private List<SharePoint> ReadPoints(EtfKey key) => facts.Points(key);
    private List<CollectionAttempt> CollectionAttempts(EtfKey key, DateOnly from, DateOnly to) => facts.CollectionAttempts(key, from, to);
    private bool? IsReviewedTradingDay(string market, DateOnly day) => facts.IsReviewedTradingDay(market, day);
}
