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
    [NationalTeamQuery("market-breadth-history",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryMarketBreadthHistory(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} breadthFrom||request.To is not {} breadthThrough)throw new InvalidDataException("广度档案需要日期区间。");
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(await workspace!.Cycle.MarketBreadthHistoryAsync(breadthFrom,breadthThrough,request.KnowledgeCutoff,token),token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("market-breadth-receipt",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryMarketBreadthReceipt(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(!Guid.TryParse(request.Revision,out var breadthRequest)||request.BreadthSubmission is not {} submittedBreadth)throw new InvalidDataException("广度回执查询参数无效。");
        rows=[SerializeRow(await workspace!.Cycle.MarketBreadthSubmissionReceiptAsync(breadthRequest,submittedBreadth,token))];
        return new(rows,metadata);
    }

    [NationalTeamQuery("market-cycle-study",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryMarketCycleStudy(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.To is not {} cycleAsOf)throw new InvalidDataException("周期研究需要统计日期。");
        var cycle=await workspace!.Cycle.MarketCycleStudyAsync(cycleAsOf,request.KnowledgeCutoff,token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(cycle,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("activity-knowledge",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryActivityKnowledge(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} activityFrom||request.To is not {} activityThrough)throw new InvalidDataException("成交知识研究需要完整日期区间。");
        var activityKnowledge=await workspace!.Activity.MarketActivityKnowledgeAsync(activityFrom,activityThrough,request.KnowledgeCutoff,token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(activityKnowledge,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("index-series-report",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryIndexSeriesReport(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} indexFrom||request.To is not {} indexThrough)throw new InvalidDataException("指数研究需要完整日期区间。");
        var indexReport=await workspace!.Index.IndexSeriesReportAsync(indexFrom,indexThrough,token);
        indexReport=indexReport with{PriceCollectionAllowed=IndexPriceCollectionAllowed(),ValuationCollectionAllowed=IndexValuationCollectionAllowed()};
        metadata=JsonSerializer.Serialize(indexReport with{Rows=[]});rows=indexReport.Rows.Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("index-series-revisions",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryIndexSeriesRevisions(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(request.From is not {} indexRevisionFrom||request.To is not {} indexRevisionThrough)throw new InvalidDataException("指数修订需要完整日期区间。");
        rows=(await workspace!.Index.IndexSeriesRevisionsAsync(indexRevisionFrom,indexRevisionThrough,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("market-activity",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryMarketActivity(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Activity.MarketActivityAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("activity-revisions",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryActivityRevisions(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Activity.MarketActivityRevisionsAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamJob("market-breadth-submit","广度档案登记（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=14)]
    private async Task<BackgroundJobResult> ExecuteMarketBreadthSubmit(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="market-breadth-submit"&&request.BreadthSubmission is {} breadthSubmission&&commandId is {} breadthCommand))throw new InvalidDataException("任务与业务参数不匹配。");
        return await SubmitMarketBreadthAsync(breadthSubmission,breadthCommand,token);
    }

    [NationalTeamJob("market-breadth-review","广度原件与修订核验（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=15)]
    private async Task<BackgroundJobResult> ExecuteMarketBreadthReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="market-breadth-review"&&request.BreadthReview is {} breadthReview&&commandId is {} breadthReviewCommand))throw new InvalidDataException("任务与业务参数不匹配。");
        try{await workspace!.Cycle.ReviewMarketBreadthAsync(breadthReview,breadthReviewCommand,token);return BackgroundJobResult.Success("breadth=Reviewed");}
        catch(InvalidDataException){return new("Rejected","breadth=ReviewInvalidOrChanged");}
    }

    [NationalTeamJob("index-series-sync","国证指数日线补缺（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=20)]
    private async Task<BackgroundJobResult> ExecuteIndexSeriesSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        return await SyncIndexSeriesAsync(request,token,commandId);
    }

    [NationalTeamJob("index-valuation-sync","国证行业指数当期估值（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=21)]
    private async Task<BackgroundJobResult> ExecuteIndexValuationSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        return await SyncIndexValuationAsync(request,token,commandId);
    }

    [NationalTeamJob("index-series-review","指数点位及估值修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=22)]
    private async Task<BackgroundJobResult> ExecuteIndexSeriesReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="index-series-review"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Index.ReviewIndexSeriesAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success();
    }

    [NationalTeamJob("activity-review","成交统计修订审核（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=13)]
    private async Task<BackgroundJobResult> ExecuteActivityReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="activity-review"))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Activity.ReviewMarketActivityAsync(request.Revision??"",request.Accept,request.Reviewer??"",token,commandId);return BackgroundJobResult.Success();
    }

    [NationalTeamJob("market-activity-sync","沪深股票成交统计同步","0 30 23 * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=2)]
    private async Task<BackgroundJobResult> ExecuteMarketActivitySync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation is "sync" or "market-sync"))throw new InvalidDataException("任务与业务参数不匹配。");
        var activity=request.From is null && request.To is null?await workspace!.Activity.SyncRecentMarketActivityAsync(token):null;
        if(activity is null)
        {
            if(request.From is null || request.To is null)throw new InvalidDataException("请填写完整成交统计日期区间。");
            var manualActivity=await workspace!.Activity.SyncMarketActivityReportAsync(request.From.Value,request.To.Value,token);
            return ActivityResult(manualActivity);
        }
        return ActivityResult(activity);
    }
}
