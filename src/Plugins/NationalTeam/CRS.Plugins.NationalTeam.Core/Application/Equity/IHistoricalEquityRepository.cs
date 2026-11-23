using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>历史原文独立能力；旧存储提供方可以显式不支持，不能以当前SSE候选代替历史披露。</summary>
public interface IHistoricalEquityReadRepository
{
    IReadOnlyList<HistoricalEquityPdf> HistoricalEquityDocuments();
    HistoricalEquityReceipt? HistoricalEquityReceipt(Guid requestId);
    bool HistoricalEquityRequestMatches(Guid requestId,string definitionId,string reviewer,string reason);
}
public interface IHistoricalEquityWriteRepository
{
    HistoricalEquityReceipt ImportHistoricalEquityPdf(string definitionId,byte[] document,string reviewer,string reason,Guid requestId);
}
