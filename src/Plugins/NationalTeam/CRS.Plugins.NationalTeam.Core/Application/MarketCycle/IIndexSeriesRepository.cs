using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>只由后台取得的冻结原件；Role区分方法、单位页面、日线和估值日期前后证明，不接收桌面路径。</summary>
public sealed record CapturedIndexDocument(string Role,string Url,byte[] Content,DateTimeOffset RetrievedAt,string Sha256);
public sealed record CapturedIndexPacket(string Kind,string? Code,DateOnly From,DateOnly Through,IReadOnlyList<CapturedIndexDocument> Documents);
public sealed record IndexImportOutcome(string CaptureId,int Added,int Duplicates,int Pending,DateTimeOffset RegisteredAt)
{
    // 命令回执同时绑定金融种类、代码和区间，不能用价格或另一日期的回执跳过当前估值采集。
    public string Kind {get;init;}="";
    public string? Code {get;init;}
    public DateOnly From {get;init;}
    public DateOnly Through {get;init;}
}
public sealed record IndexCalendarSnapshot(IReadOnlyDictionary<DateOnly,bool?> Days,string Fingerprint);
public interface IIndexSeriesReadRepository
{
    IndexImportOutcome? IndexSeriesReceipt(Guid commandId);
    IReadOnlyList<IndexSeriesRevision> IndexSeriesRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff);
    IndexCalendarSnapshot IndexSeriesCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff);
}
/// <summary>原件关联、修订、追加事件与双回执同事务提交；取消或冲突不留下半批事实。</summary>
public interface IIndexSeriesWriteRepository
{
    IndexImportOutcome ImportIndexSeries(CapturedIndexPacket packet,Guid? commandId=null);
    void ReviewIndexSeries(string revision,bool accept,string reviewer,Guid? commandId=null);
}
