using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Serialization;
namespace CRS.Plugins.NationalTeamWatch;
public sealed partial class WorkerEtfWorkspace
{
    public async Task<JointExitStudyReport> JointExitStudyAsync(EtfKey key,DateOnly asOf,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("joint-exit-study",key.Market,key.Ticker,To:asOf,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<JointExitStudyReport>(blocks,token);
        if(report.FormatVersion!=JointExitStudyReport.Version||report.Input.Configuration.Market!=key.Market||report.Input.Configuration.Ticker!=key.Ticker||report.Input.AsOfDate!=asOf)throw new InvalidDataException("联合观察证券或日期不符。");
        var rebuilt=JointExitStudy.Evaluate(report.Input,report.Rules);
        if(rebuilt.Fingerprint!=report.Fingerprint||rebuilt.State!=report.State||JsonSerializer.Serialize(rebuilt.Components)!=JsonSerializer.Serialize(report.Components))throw new InvalidDataException("联合观察输入或评价结果不符。");return rebuilt;
    }
}
