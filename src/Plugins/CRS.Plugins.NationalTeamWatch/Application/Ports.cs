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
    List<EvidenceRow> Evidence();
    List<SharePoint> Points(EtfKey key);
    CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to);
    string RecordFetch(string hash,string sourceUrl,string? originalHash=null);
    void MarkFailed(string fetchId);
    void ImportCalendar(string fetchId,IReadOnlyList<(string Market,DateOnly Date)> days,CancellationToken cancellation);
    int ImportShares(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation);
    void Review(string revisionId,bool accept,string reviewer,bool comparableConfirmed=false);
    void SetMembership(EtfKey key,string name,string index,bool active,string reason);
}
