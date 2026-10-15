using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>联网采集端口；每个响应单独保留，不能把失败或截断的页面当作完整交易日。</summary>
public sealed record SourcePage(Uri Uri,byte[] Content,IReadOnlyList<ImportRow> Rows,string Market="SSE");
public sealed record SourceDay(DateOnly Date,IReadOnlyList<SourcePage> Pages,IReadOnlyList<string>? Issues=null);
public sealed record SyncReport(int QueriedDays,int SourceIssues);
public interface IOfficialShareSource
{
    Task<SourceDay> FetchAsync(DateOnly day,CancellationToken cancellationToken);
    Task<SourceDay> FetchAsync(DateOnly day,IReadOnlyList<EtfKey> pool,CancellationToken cancellationToken)=>FetchAsync(day,cancellationToken);
}
