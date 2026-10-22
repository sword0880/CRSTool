using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
/// <summary>原文件读取、不可变存证及核验导出端口，应用层不直接操作文件。</summary>
public interface IRawEvidenceStore
{
    string Save(byte[] content);
    Task<byte[]> ReadImportFileAsync(string path,CancellationToken cancellationToken);
    Task ExportVerifiedAsync(string hash,string destination,IReadOnlyList<string> protectedDirectories,CancellationToken cancellationToken);
}
/// <summary>市场数据与修订仓储端口；不允许访问宿主税务数据库。</summary>
public interface IMarketRepository : IDisposable
{
    /// <summary>串行用例把取消传入同步原件核验；取消后的未完整查询不能返回部分金融结果。</summary>
    T ExecuteOperation<T>(Func<T> work,CancellationToken token)=>work();
    ExitAlertOverview EvaluateExitAlerts(bool force=false)=>throw new NotSupportedException();
    ExitAlertOverview AlertOverview()=>throw new NotSupportedException();
    ExitAlertConfiguration AlertConfiguration()=>throw new NotSupportedException();
    void SaveAlertConfiguration(ExitAlertConfiguration value,Guid? commandId=null)=>throw new NotSupportedException();
    List<ExitAlertEvent> AlertEvents(ExitAlertFilter filter)=>throw new NotSupportedException();
    ExitAlertEvent? AlertEvent(string id)=>throw new NotSupportedException();
    ExitAlertEvent? AlertEvent(string id,int? sequence)=>AlertEvent(id);
    List<EvidenceRow> AlertInputs(string id,int? sequence=null)=>throw new NotSupportedException();
    List<ExitAlertChange> AlertChanges(string id)=>throw new NotSupportedException();
    List<AlertOutboxItem> AlertOutbox(ExitAlertFilter? filter=null)=>throw new NotSupportedException();
    AlertOutboxItem? ClaimAlertNotification()=>throw new NotSupportedException();
    void CompleteAlertNotification(string id,string lease,AlertDispatchOutcome outcome)=>throw new NotSupportedException();
    void RetryAlertNotification(string id,Guid? commandId=null)=>throw new NotSupportedException();
    List<AlertDeliveryRecord> AlertDeliveries(string id)=>throw new NotSupportedException();
    List<AlertDocument> AlertDocuments(EtfKey key)=>throw new NotSupportedException();
    void RegisterAlertDocument(AlertDocumentRequest request,byte[] document,Guid? commandId=null)=>throw new NotSupportedException();
    List<AlertFundMetadata> AlertMetadata()=>throw new NotSupportedException();
    void ReviewAlertMetadata(AlertMetadataReview review,Guid? commandId=null)=>throw new NotSupportedException();
    List<NamedEtfHolding> NamedEtfHoldings(EtfKey? key=null)=>throw new NotSupportedException();
    void ReviewNamedEtfHolding(NamedEtfHoldingReview review,Guid? commandId=null)=>throw new NotSupportedException();
    string BeginCollection(EtfKey key,DateOnly day)=>throw new NotSupportedException();
    void RecordCollection(string id,string state,string reason,string? fetchId=null)=>throw new NotSupportedException();
    List<CollectionAttempt> CollectionAttempts(EtfKey key,DateOnly from,DateOnly to)=>throw new NotSupportedException();
    List<ShareGap> DueShareGaps(DateOnly from,DateOnly to,DateTimeOffset now)=>[];
    List<ShareGap> DueHistoricalShareGaps(DateOnly through,DateTimeOffset now)=>[];
    List<EtfAcceptance> Acceptance(DateOnly through)=>throw new NotSupportedException();
    List<EtfAcceptance> Acceptance(EtfKey key,DateOnly through)=>Acceptance(through).Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker).ToList();
    List<EtfSummary> Overview();
    List<MarketActivityPoint> MarketActivity()=>throw new NotSupportedException();
    HashSet<(string Market,DateOnly Date)> VerifiedMarketActivityDates(DateOnly from,DateOnly to)=>[];
    void SaveMarketActivity(string market,DateOnly day,MarketActivitySource? source)=>throw new NotSupportedException();
    DateOnly? ReadMarketActivityCursor()=>null;
    void RecordMarketActivityCursor(DateOnly day)=>throw new NotSupportedException();
    List<MarketActivityRevision> MarketActivityRevisions()=>throw new NotSupportedException();
    void ReviewMarketActivity(string id,bool accept,string reviewer,Guid? commandId=null)=>throw new NotSupportedException();
    void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri)=>throw new NotSupportedException();
    bool? IsReviewedTradingDay(string market,DateOnly day)=>null;
    List<EquityWatch> EquityWatchlist()=>throw new NotSupportedException();
    void SaveEquityWatch(EquityWatch watch,Guid? commandId=null)=>throw new NotSupportedException();
    void SaveEquityDisclosure(string market,string ticker,Uri uri,byte[] bytes)=>throw new NotSupportedException();
    List<EquityHoldingRow> EquityHoldings(string? search,string? institution,string? nature,bool favorites)=>throw new NotSupportedException();
    List<EquityHoldingRow> EquityEvents(EtfKey key)=>throw new NotSupportedException();
    List<EquityReportDocument> EquityReports(EtfKey key)=>throw new NotSupportedException();
    void SaveEquityReports(EtfKey key,DateOnly from,DateOnly to,IReadOnlyList<byte[]> pages,Guid? commandId=null)=>throw new NotSupportedException();
    bool EquityReportCommandComplete(Guid id)=>false;
    List<EquityDisclosure> EquityDisclosures(EtfKey key)=>throw new NotSupportedException();
    void ReviewEquity(EquityReview review,byte[] document,Guid? commandId=null)=>throw new NotSupportedException();
    /// <summary>仅后台完整性作业执行持久状态协调；读查询不得调用。</summary>
    void ReconcileEvidenceIntegrity()=>throw new NotSupportedException();
    List<EvidenceRow> Evidence();
    /// <summary>生产仓储在查询时过滤证券；默认实现供已有隔离夹具兼容。</summary>
    List<EvidenceRow> Evidence(EtfKey key)=>Evidence().Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker).ToList();
    List<EtfHistoryPoint> History(EtfKey? key)=>throw new NotSupportedException();
    List<SharePoint> Points(EtfKey key);
    /// <summary>同一批事实共享一次原件完整性核验，避免采集提交期间逐证券重复扫描全库。</summary>
    Dictionary<EtfKey,List<SharePoint>> PointsForKeys(IReadOnlyList<EtfKey> keys)=>keys.ToDictionary(x=>x,Points);
    CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to);
    DateOnly? ReadSyncCursor();
    void RecordSyncCursor(DateOnly day);
    string RecordFetch(string hash,string sourceUrl,string? originalHash=null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1");
    void MarkFailed(string fetchId);
    void ImportCalendar(string fetchId,IReadOnlyList<(string Market,DateOnly Date)> days,CancellationToken cancellation);
    int ImportShares(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation,bool requireCalendar=true);
    ShareImportOutcome ImportSharesDetailed(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation,bool requireCalendar=true);
    void Review(string revisionId,bool accept,string reviewer,bool comparableConfirmed=false,Guid? commandId=null);
    void SetMembership(EtfKey key,string name,string index,bool active,string reason,Guid? commandId=null);
    List<EtfConfiguration> Configuration();
    void SaveConfiguration(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,Guid? commandId=null);
}
