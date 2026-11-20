using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF导入与审核写入；由应用层控制提交边界。</summary>
public interface IEtfWriteRepository
{
    void RegisterReviewedCalendar(string market,int year,byte[] content,Uri uri);
    string RecordFetch(string hash,string sourceUrl,string? originalHash=null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1");
    void MarkFailed(string fetchId);
    void ImportCalendar(string fetchId,IReadOnlyList<(string Market,DateOnly Date)> days,CancellationToken cancellation);
    int ImportShares(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation,bool requireCalendar=true);
    ShareImportOutcome ImportSharesDetailed(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation,bool requireCalendar=true);
    void Review(string revisionId,bool accept,string reviewer,bool comparableConfirmed=false,Guid? commandId=null);
    void SetMembership(EtfKey key,string name,string index,bool active,string reason,Guid? commandId=null);
    void SaveConfiguration(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,Guid? commandId=null);
}
