using System.IO;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Serialization;

namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    public async Task<EquityDirectionReport> EquityDirectionAsync(DateOnly? reportAsOf,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("equity-direction",To:reportAsOf,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<EquityDirectionReport>(blocks,token);
        if(report.FormatVersion!=EquityDirectionReport.Version||reportAsOf is not null&&report.ReportAsOf!=reportAsOf
            ||report.ActiveSecurities<report.DisclosedSecurities||report.ClassifiedSecurities>report.DisclosedSecurities||report.PricedSecurities>report.ClassifiedSecurities
            ||report.ReviewedAccounts!=report.Positions.Count||report.Positions.Any(x=>x.Holding.ReportAsOf!=report.ReportAsOf||x.Holding.EvidenceLevel!=EvidenceLevel.DisclosedHolding))
            throw new InvalidDataException("持仓方向报告版本、期间或完整性不符。");
        return report;
    }
    public Task ReviewEquityDirectionAsync(EquityDirectionReview review,CancellationToken token)
        =>RunAsync("equity-direction-review",new("equity-direction-review",EquityDirectionReview:review),token);
}
