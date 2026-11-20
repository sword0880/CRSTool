using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>预警只读取ETF事实的显式边界；不暴露总仓储或调度、通知发送能力。</summary>
internal interface IAlertMarketFacts
{
    List<EtfAcceptance> Acceptance(DateOnly through);
    List<EtfConfiguration> Configuration();
    List<EvidenceRow> Evidence();
    List<(DateOnly Date, bool Valid)> Calendar(string market);
    List<SharePoint> Points(EtfKey key);
    List<CollectionAttempt> CollectionAttempts(EtfKey key, DateOnly from, DateOnly to);
    bool? IsReviewedTradingDay(string market, DateOnly day);
}
