using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>日期来自固定官网文件，取得时间由后台登记；不接收桌面文件路径或自报审核时间。</summary>
public sealed record CapturedIndustryPeDocument(string Url,DateOnly Date,byte[] Content,DateTimeOffset RetrievedAt,string Sha256);
public sealed record IndustryPeImportOutcome(string CaptureId,int Added,int Duplicates,int Pending,DateTimeOffset RegisteredAt);
public sealed record IndustryPeCalendarSnapshot(IReadOnlyDictionary<DateOnly,bool?> Days,string Fingerprint);
public interface IIndustryPeReadRepository
{
    IndustryPeImportOutcome? IndustryPeReceipt(Guid commandId);
    IReadOnlyList<IndustryPeRevision> IndustryPeRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff);
    IndustryPeCalendarSnapshot IndustryPeCalendar(DateOnly from,DateOnly through,DateTimeOffset cutoff);
}
/// <summary>原件、修订、追加事件和命令双回执必须单事务提交；未知实现不能默认返回空集合。</summary>
public interface IIndustryPeWriteRepository
{
    IndustryPeImportOutcome ImportIndustryPe(CapturedIndustryPeDocument document,Guid? commandId=null);
    void ReviewIndustryPe(string revision,bool accept,string reviewer,Guid? commandId=null);
}
