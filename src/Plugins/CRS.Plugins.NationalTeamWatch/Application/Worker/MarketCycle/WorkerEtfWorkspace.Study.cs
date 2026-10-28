using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Serialization;
namespace CRS.Plugins.NationalTeamWatch;
public sealed partial class WorkerEtfWorkspace
{
    public async Task<MarketCycleStudyReport> MarketCycleStudyAsync(DateOnly asOf,DateTimeOffset? cutoff,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("market-cycle-study",To:asOf,KnowledgeCutoff:cutoff,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<MarketCycleStudyReport>(blocks,token);
        if(report.FormatVersion!=MarketCycleStudyReport.Version||report.Input.AsOfDate!=asOf||report.Input.Market!="SSE+SZSE"||report.Input.IndexCode!="399317"
            ||cutoff.HasValue&&report.Input.EvaluationCutoff!=cutoff.Value)throw new InvalidDataException("市场周期报告身份或截止时间不符。");
        var verified=MarketCycleStudyReport.Freeze(report.Input,report.Sources,report.ActivityKnowledgeFingerprint,report.Rules);
        if(verified.Fingerprint!=report.Fingerprint||JsonSerializer.Serialize(verified.Assessment)!=JsonSerializer.Serialize(report.Assessment))throw new InvalidDataException("市场周期输入、计算结果或原件引用指纹不符。");
        return verified;
    }
}
