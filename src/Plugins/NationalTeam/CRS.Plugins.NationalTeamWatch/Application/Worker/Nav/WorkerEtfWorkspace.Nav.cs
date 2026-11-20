// NAV研究的远程客户端：按证券和日期读取后台冻结结果，保持分页完整性，界面端不计算正式净值。
using System.IO;
using CRS.Serialization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    public async Task<List<NavAcceptedPoint>> NavPointsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
        =>(await ReadPagesAsync<NavAcceptedPoint>(new("nav-points",key.Market,key.Ticker,from,through),token)).Rows;
    public async Task<List<NavRevision>> NavRevisionsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
        =>(await ReadPagesAsync<NavRevision>(new("nav-revisions",key.Market,key.Ticker,from,through),token)).Rows;
    public async Task<EtfNavStudyReport> NavStudyAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
    {
        var result=await ReadPagesAsync<JsonDocumentBlock>(new("nav-study",key.Market,key.Ticker,from,through,Count:2),token);
        var report=JsonDocumentBlocks.Decode<EtfNavStudyReport>(result.Rows,token);
        if(report.AlgorithmVersion!=EtfNavStudy.AlgorithmVersion||report.FrozenInputs.Key!=key||report.FrozenInputs.From!=from||report.FrozenInputs.Through!=through)
            throw new InvalidDataException("NAV研究证券或日期身份不符，拒绝显示串线结果。");
        return report;
    }
}
