using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>将旧ETF私有读取适配到预警窄端口；同一会话内读取，不另开连接或提交事务。</summary>
    private sealed class AlertMarketFacts(MarketRepository owner) : IAlertMarketFacts
    {
        public List<EtfAcceptance> Acceptance(DateOnly through) => owner.Acceptance(through);
        public List<EtfConfiguration> Configuration() => owner.Configuration();
        public List<EvidenceRow> Evidence() => owner.ReadEvidence();
        public List<(DateOnly Date, bool Valid)> Calendar(string market) => owner.ReadCalendar(market);
        public List<SharePoint> Points(EtfKey key) => owner.ReadPoints(key);
        public List<CollectionAttempt> CollectionAttempts(EtfKey key, DateOnly from, DateOnly to) => owner.CollectionAttempts(key, from, to);
        public bool? IsReviewedTradingDay(string market, DateOnly day) => owner.IsReviewedTradingDay(market, day);
    }
}
