// 市场广度资料提交、复核与历史查询的数据合同；原件和规范化文件分别绑定暂存身份、指纹及记录时点。
using CRS.IO.Staging;
using CRS.Common.Integrity;
using System.Text.Json;
namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record MarketBreadthSubmission(StagedDocumentReference Original,StagedDocumentReference Normalized,
    string SourceUrl,DateTimeOffset RetrievedAt,string Submitter,string Reason);
public sealed record MarketBreadthReview(string RevisionId,DateOnly Date,string ExpectedFingerprint,bool Accept,
    string Reviewer,string Reason,bool OriginalConfirmed);
public sealed record MarketBreadthSubmissionReceipt(Guid RequestId,string RevisionId,DateOnly Date,DateTimeOffset RegisteredAt,
    string OriginalHash,string NormalizedHash);
public sealed record MarketBreadthRevision(string Id,DateOnly Date,string UniverseSnapshotId,int UniverseCount,int AboveMa20Count,int AboveMa60Count,int NewLowCount,
    string UniverseHash,string DefinitionVersion,string OriginalHash,string NormalizedHash,string SourceUrl,DateTimeOffset RetrievedAt,
    DateTimeOffset RegisteredAt,DateTimeOffset StateRecordedAt,string Decision,MarketCycleObservationState State,bool EvidenceHealthy,
    string Reviewer,string Reason,string StateEventHash,string? StateCommandId)
{
    public string StateText=>!EvidenceHealthy?"原件异常":Decision switch{"Accepted"=>"已核验","NeedsReview"=>"待核验","Rejected"=>"已否决","Superseded"=>"已替代",_=>"状态异常"};
}
public sealed record MarketBreadthHistory(DateOnly From,DateOnly Through,DateTimeOffset Cutoff,IReadOnlyList<MarketBreadthRevision> Rows,string StateFingerprint)
{
    public const string Version="market-breadth-history-v1";
    public string FormatVersion {get;init;}=Version;
    public static MarketBreadthHistory Freeze(DateOnly from,DateOnly through,DateTimeOffset cutoff,IReadOnlyList<MarketBreadthRevision> rows)
    {
        rows=rows.OrderBy(x=>x.Date).ThenBy(x=>x.Id,StringComparer.Ordinal).Select(x=>x with{RetrievedAt=CRS.Timing.BeijingClock.Normalize(x.RetrievedAt),RegisteredAt=CRS.Timing.BeijingClock.Normalize(x.RegisteredAt),StateRecordedAt=CRS.Timing.BeijingClock.Normalize(x.StateRecordedAt)}).ToArray();
        return new(from,through,CRS.Timing.BeijingClock.Normalize(cutoff),rows,ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(new{Version,From=from,Through=through,Rows=rows})));
    }
}
