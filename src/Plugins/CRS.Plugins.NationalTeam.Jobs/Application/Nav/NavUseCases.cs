using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class NavUseCases : MarketUseCaseScope
{
    internal NavUseCases(MarketApplicationSession session):base(session){}
    public Task<NavImportOutcome?> NavImportReceiptAsync(Guid id, CancellationToken token)
        => ReadPortAsync<INavReadRepository, NavImportOutcome?>(r => r.NavImportReceipt(id), token);
    /// <summary>联网已在写入门槛之外完成，整个原件组与命令回执在仓储单事务提交。</summary>
    public Task<NavImportOutcome> ImportNavDocumentsAsync(IReadOnlyList<CapturedNavDocument> documents, CancellationToken token, Guid? commandId = null)
        => RunAsync((INavWriteRepository repository)=> repository!.ImportNavDocuments(documents, commandId), token);
    public Task ReviewNavAsync(string revision, bool accept, string reviewer, CancellationToken token, Guid? commandId = null)
        => RunAsync((INavWriteRepository repository)=> { repository!.ReviewNavRevision(revision, accept, reviewer, commandId); return true; }, token);
    /// <summary>每次查询固定当前知识截止，分页后续读取沿用上层冻结快照。</summary>
    public Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key, DateOnly from, DateOnly through, CancellationToken token)
        => ReadPortAsync<INavReadRepository, List<NavAcceptedPoint>>(r => r.NavPoints(key, from, through, DateTimeOffset.UtcNow).ToList(), token);
    public Task<List<NavRevision>> NavRevisionsAsync(EtfKey key, DateOnly from, DateOnly through, CancellationToken token)
        => ReadPortAsync<INavReadRepository, List<NavRevision>>(r => r.NavRevisions(key, from, through, DateTimeOffset.UtcNow).ToList(), token);
}
