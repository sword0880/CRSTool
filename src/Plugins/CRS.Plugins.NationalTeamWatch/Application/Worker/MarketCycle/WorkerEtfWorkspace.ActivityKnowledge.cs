using System.IO;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Serialization;

namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    public async Task<MarketActivityKnowledgeReport> MarketActivityKnowledgeAsync(DateOnly from,DateOnly through,DateTimeOffset? cutoff,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("activity-knowledge",From:from,To:through,KnowledgeCutoff:cutoff,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<MarketActivityKnowledgeReport>(blocks,token);
        if(report.FormatVersion!=MarketActivityKnowledgeReport.Version||report.From!=from||report.Through!=through||cutoff.HasValue&&report.Cutoff!=cutoff.Value
            ||report.Revisions.Any(x=>x.Date<from||x.Date>through||x.FirstSeenAt>report.Cutoff||x.StateRecordedAt>report.Cutoff||x.SourceRetrievedAt>x.FirstSeenAt)
            ||MarketActivityKnowledgeReport.Freeze(report.From,report.Through,report.Cutoff,report.Revisions).Fingerprint!=report.Fingerprint)
            throw new InvalidDataException("成交知识报告区间、截止时点或完整指纹不符。");
        return report;
    }
}
