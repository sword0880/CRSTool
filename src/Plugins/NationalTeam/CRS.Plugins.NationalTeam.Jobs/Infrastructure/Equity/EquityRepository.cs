using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    // 聚合入口不再持有直接持股SQL；领域Store沿用同一会话及原事务提交点。
    public List<EquityWatch> EquityWatchlist() => equityStore.EquityWatchlist();
    public void SaveEquityWatch(EquityWatch watch, Guid? commandId = null) => equityStore.SaveEquityWatch(watch, commandId);
    public void SaveEquityDisclosure(string market, string ticker, Uri uri, byte[] bytes) => equityStore.SaveEquityDisclosure(market, ticker, uri, bytes);
    public List<EquityDisclosure> EquityDisclosures(EtfKey key) => equityStore.EquityDisclosures(key);
    public void ReviewEquity(EquityReview review, byte[] document, Guid? commandId = null) => equityStore.ReviewEquity(review, document, commandId);
    public List<EquityHoldingRow> EquityHoldings(string? search, string? institution, string? nature, bool favorites) => equityStore.EquityHoldings(search, institution, nature, favorites);
    public List<EquityHoldingRow> EquityEvents(EtfKey key) => equityStore.EquityEvents(key);
    public EquityDirectionReport EquityDirection(DateOnly? reportAsOf)=>equityStore.EquityDirection(reportAsOf);
    public void ReviewEquityDirection(EquityDirectionReview review,Guid commandId)=>equityStore.ReviewEquityDirection(review,commandId);
}
