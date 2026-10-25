namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>
/// 指数来源审批由宿主提供；价格与估值分别批准，不能用接口可访问或采集任务授权代替来源用途证据。
/// 未配置提供者或缺少记录时，后台在任何HTTP及金融写入前失败关闭；查询既有事实不依赖此审批。
/// </summary>
public interface IIndexSeriesSourceApprovals : IMarketCycleSourceApprovalEvidenceVerifier
{
    MarketCycleSourceApproval? Find(string datasetId);
}
