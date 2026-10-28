using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>本功能声明自己的公开查询、持久任务及执行组，入口仅负责授权协议和生命周期。</summary>
public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("etf-holding-history",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEtfHoldingHistory(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.To is not {} holdingHistoryDate||request.KnowledgeCutoff is not null)throw new InvalidDataException("具名ETF历史只提供当前已核快照，不支持重建历史审核知识。");
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(await workspace!.Holdings.HoldingHistoryAsync(Key(request),holdingHistoryDate,token),token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("etf-holder-candidates",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEtfHolderCandidates(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        var holderCandidates=await workspace!.Alerts.EtfHolderCandidatesAsync(Key(request),request.Revision??"",token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(holderCandidates,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("etf-report-index",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEtfReportIndex(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        var etfReports=await workspace!.Holdings.ReportIndexAsync(Key(request),token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(etfReports,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("etf-report-search","查找配置ETF官方报告（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=12)]
    private async Task<BackgroundJobResult> ExecuteEtfReportSearch(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="etf-report-search"))throw new InvalidDataException("任务与业务参数不匹配。");
        return await SearchEtfReportsAsync(request,commandId,token);
    }
}
