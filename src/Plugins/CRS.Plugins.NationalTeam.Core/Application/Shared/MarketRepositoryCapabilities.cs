namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>正式 Worker 必须具备的存储能力；在任何计划作业开放前失败，不能带病启动。</summary>
public static class MarketRepositoryCapabilities
{
    public static void Validate(IMarketRepository repository, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireCapabilities(repository);
        // 真实打开独立只读会话，防止仅写连接可用却把查询能力缺失拖到首次用户请求。
        ((IMarketReadSessions)repository).Read(reader =>
        {
            token.ThrowIfCancellationRequested();
            RequireCapabilities(reader, requireSessions: false);
            return true;
        }, token);
    }

    private static void RequireCapabilities(IMarketRepository repository, bool requireSessions = true)
    {
        if (repository is not IBoundedMarketEvidence || repository is not IEtfDashboardRepository ||
            repository is not IEvidenceIntegrityRepository || repository is not IExitAlertEvaluationRepository || repository is not IEtfCollectionEvidenceRepository || requireSessions && repository is not IMarketReadSessions)
            throw new NotSupportedException("市场仓储缺少必需的独立读取、有界证据、总览或完整性巡检能力。");
    }
}
