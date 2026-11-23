using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>直接持股披露、报告索引与命令状态读取。</summary>
public interface IEquityReadRepository
{
    List<EquityWatch> EquityWatchlist();
    List<EquityHoldingRow> EquityHoldings(string? search,string? institution,string? nature,bool favorites);
    List<EquityHoldingRow> EquityEvents(EtfKey key);
    List<EquityReportDocument> EquityReports(EtfKey key);
    bool EquityReportCommandComplete(Guid id);
    List<EquityDisclosure> EquityDisclosures(EtfKey key);
    EquityDirectionReport EquityDirection(DateOnly? reportAsOf);
}
