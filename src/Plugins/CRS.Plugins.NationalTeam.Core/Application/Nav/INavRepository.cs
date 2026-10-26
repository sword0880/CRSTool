using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record CapturedNavDocument(string Url, byte[] Content, DateTimeOffset RetrievedAt, string Sha256);
public sealed record NavImportOutcome(string CaptureId, int Added, int Duplicates, int Pending, DateTimeOffset RegisteredAt);
public sealed record NavRevision(string Id, string Market, string Ticker, DateOnly Date, decimal UnitNavYuan, decimal AccumulatedNavYuan,
    int Revision, string State, DateTimeOffset FirstSeenAt, DateTimeOffset StateRecordedAt, string CaptureId, bool EvidenceHealthy);
public sealed record NavAcceptedPoint(DateOnly Date, decimal UnitNavYuan, decimal AccumulatedNavYuan, string RevisionId,
    DateTimeOffset FirstSeenAt, DateTimeOffset AcceptedAt, string CaptureId);

/// <summary>独立NAV修订及知识时点读取；不混入份额、日历或具名持仓。</summary>
public interface INavReadRepository
{
    /// <summary>采集重试先读取已提交回执，避免重新联网后用新取得时间改写同一命令身份。</summary>
    NavImportOutcome? NavImportReceipt(Guid commandId);
    IReadOnlyList<NavRevision> NavRevisions(EtfKey key, DateOnly from, DateOnly through, DateTimeOffset knowledgeCutoff);
    IReadOnlyList<NavAcceptedPoint> NavPoints(EtfKey key, DateOnly from, DateOnly through, DateTimeOffset knowledgeCutoff);
}
/// <summary>严格官方原件导入与显式修订审核；不提供默认空实现。</summary>
public interface INavWriteRepository
{
    NavImportOutcome ImportNavDocuments(IReadOnlyList<CapturedNavDocument> documents, Guid? commandId = null);
    void ReviewNavRevision(string revisionId, bool accept, string reviewer, Guid? commandId = null);
}
