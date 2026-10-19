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
    List<EtfSummary> Overview();
    List<MarketActivityPoint> MarketActivity()=>throw new NotSupportedException();
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
    List<EquityDisclosure> EquityDisclosures(EtfKey key)=>throw new NotSupportedException();
    void ReviewEquity(EquityReview review,byte[] document,Guid? commandId=null)=>throw new NotSupportedException();
    List<EvidenceRow> Evidence();
    List<EtfHistoryPoint> History(EtfKey? key)=>throw new NotSupportedException();
    List<SharePoint> Points(EtfKey key);
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
