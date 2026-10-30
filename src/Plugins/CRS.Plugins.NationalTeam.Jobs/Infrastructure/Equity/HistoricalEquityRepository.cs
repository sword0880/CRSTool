// 机构股票历史披露的仓储转发，目录与原件审核记录交由历史持股存储管理。
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository : IHistoricalEquityReadRepository, IHistoricalEquityWriteRepository
{
    public IReadOnlyList<HistoricalEquityPdf> HistoricalEquityDocuments() => equityStore.HistoricalEquityDocuments();
    public bool HistoricalEquityRequestMatches(Guid requestId, string definitionId, string reviewer, string reason) => equityStore.HistoricalEquityRequestMatches(requestId, definitionId, reviewer, reason);
    public HistoricalEquityReceipt? HistoricalEquityReceipt(Guid requestId) => equityStore.HistoricalEquityReceipt(requestId);
    public HistoricalEquityReceipt ImportHistoricalEquityPdf(string definitionId, byte[] document, string reviewer, string reason, Guid requestId) => equityStore.ImportHistoricalEquityPdf(definitionId, document, reviewer, reason, requestId);
}
