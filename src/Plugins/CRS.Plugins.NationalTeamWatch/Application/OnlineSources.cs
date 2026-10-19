using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>联网采集端口；每个响应单独保留，不能把失败或截断的页面当作完整交易日。</summary>
public sealed record SourcePage(Uri Uri,byte[] Content,IReadOnlyList<ImportRow> Rows,string Market="SSE");
public sealed record SourceFailure(string Market,string? Ticker,string State,string Reason,string? SourceUrl=null,string? OriginalHash=null,string? ParserVersion=null);
public sealed record SourceDay(DateOnly Date,IReadOnlyList<SourcePage> Pages,IReadOnlyList<string>? Issues=null,IReadOnlyList<SourceFailure>? Failures=null);
public sealed record SyncReport(int QueriedDays,int SourceIssues,int ReceivedRows=0,bool HasMore=false);
public interface IOfficialSourceValidationFeedback
{
    void RejectContract(Uri uri);
}
public interface IOfficialShareSource
{
    Task PrepareRangeAsync(DateOnly from,DateOnly to,CancellationToken token)=>Task.CompletedTask;
    Task<SourceDay> FetchAsync(DateOnly day,CancellationToken cancellationToken);
    Task<SourceDay> FetchAsync(DateOnly day,IReadOnlyList<EtfKey> pool,CancellationToken cancellationToken)=>FetchAsync(day,cancellationToken);
}
