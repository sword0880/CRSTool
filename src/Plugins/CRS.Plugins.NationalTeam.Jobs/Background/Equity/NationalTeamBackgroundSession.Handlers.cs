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
    [NationalTeamQuery("equity-watchlist",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryEquityWatchlist(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.EquityWatchlistAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-direction",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityDirection(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        var direction=await workspace!.Equity.EquityDirectionAsync(request.To,token);
        rows=CRS.Serialization.JsonDocumentBlocks.Encode(direction,token).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-holdings",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityHoldings(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.EquityHoldingsAsync(request.Search,request.Institution,request.Nature,request.Favorites,token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-disclosures",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityDisclosures(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.EquityDisclosuresAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-events",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityEvents(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.EquityEventsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-reports",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityReports(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.EquityReportsAsync(Key(request),token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-history-documents",NationalTeamQueryKind.ResearchRead)]
    private async Task<NationalTeamQueryResult> QueryEquityHistoryDocuments(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        rows=(await workspace!.Equity.HistoricalEquityDocumentsAsync(token)).Select(x=>SerializeRow(x)).ToArray();
        return new(rows,metadata);
    }

    [NationalTeamQuery("equity-history-receipt",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryEquityHistoryReceipt(EtfRequest request,CancellationToken token)
    {
        var serialization=new NationalTeamQuerySerialization(token);
        string SerializeRow<T>(T value)=>serialization.Row(value);
        string[] rows;string? metadata=null;
        if(!Guid.TryParseExact(request.Revision,"N",out var historicalReceiptId))throw new ArgumentException("历史回执身份无效。");
        var historicalReceipt=await workspace!.Equity.HistoricalEquityReceiptAsync(historicalReceiptId,token);
        rows=historicalReceipt is null?[]:[SerializeRow(historicalReceipt)];
        return new(rows,metadata);
    }

    [NationalTeamJob("equity-report-search","查找持股官方报告（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=11)]
    private async Task<BackgroundJobResult> ExecuteEquityReportSearch(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="equity-report-search"))throw new InvalidDataException("任务与业务参数不匹配。");
        return await SearchEquityReportsAsync(request,commandId,token);
    }

    [NationalTeamJob("equity-watch","直接持股观察设置（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=7)]
    private async Task<BackgroundJobResult> ExecuteEquityWatch(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="equity-watch" && request.EquityWatch is {} watch))throw new InvalidDataException("任务与业务参数不匹配。");
        await workspace!.Equity.SaveEquityWatchAsync(watch,token,commandId);return BackgroundJobResult.Success();
    }

    [NationalTeamJob("equity-direction-review","持仓方向原文核验（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="management",MaximumConcurrency=1,Order=9)]
    private async Task<BackgroundJobResult> ExecuteEquityDirectionReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="equity-direction-review"&&request.EquityDirectionReview is {} directionReview&&commandId is {} directionCommand))throw new InvalidDataException("任务与业务参数不匹配。");
        try{await workspace!.Equity.ReviewEquityDirectionAsync(directionReview,token,directionCommand);}
        catch(InvalidDataException){return new("Rejected","equityDirection=ReviewInvalid");}
        return BackgroundJobResult.Success("equityDirection=Reviewed");
    }

    [NationalTeamJob("equity-review","直接持股原文核验（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=8)]
    private async Task<BackgroundJobResult> ExecuteEquityReview(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation is "equity-review" or "equity-review-bound" && request.EquityReview is {} review))throw new InvalidDataException("任务与业务参数不匹配。");
        if(!Uri.TryCreate(review.DocumentUrl,UriKind.Absolute,out var documentUri)||!OfficialEquityDocuments.IsAllowed(documentUri))return new("Rejected","equity=OfficialPdfRequired");
        var document=await http!.GetAsync(documentUri,token);
        if(document.StatusCode!=200)return new("RetryDeferred","equity=DocumentUnavailable",TimeSpan.FromMinutes(30));
        try{await workspace!.Equity.ReviewEquityDocumentAsync(review,document.Content,token,commandId);}
        catch(InvalidDataException){return new("Rejected","equity=ReviewInvalid");}
        return BackgroundJobResult.Success();
    }

    [NationalTeamJob("equity-history-import","历史持股原文登记（手动）","0 0 0 1 1 ?","China Standard Time",false,false,true,Group="collection",MaximumConcurrency=2,Order=10)]
    private async Task<BackgroundJobResult> ExecuteEquityHistoryImport(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation=="equity-history-import"))throw new InvalidDataException("任务与业务参数不匹配。");
        if(commandId is not {} historicalId||!request.Accept||!request.Original||request.Comparable
            ||string.IsNullOrWhiteSpace(request.Reviewer)||request.Reviewer.Trim().Length>100||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length>500)
            return new("Rejected","equityHistory=ReviewRequired");
        // 只查固定目录，先检查能力和双回执；重放已提交命令无需再次下载官方原件。
        var historicalDefinitions=await workspace!.Equity.HistoricalEquityDocumentsAsync(token);
        var historicalDefinition=historicalDefinitions.SingleOrDefault(x=>x.Id==request.Revision);
        if(historicalDefinition is null)return new("Rejected","equityHistory=UnknownDefinition");
        if(await workspace.Equity.HistoricalEquityReceiptAsync(historicalId,token) is {} existingHistorical)
        {
            if(existingHistorical.ExtractHash!=historicalDefinition.ExtractHash||!await workspace.Equity.HistoricalEquityRequestMatchesAsync(historicalId,historicalDefinition.Id,request.Reviewer,request.Reason,token))
                return new("Rejected","equityHistory=RequestBindingChanged");
            return BackgroundJobResult.Success();
        }
        var historicalDocument=await http!.GetAsync(new Uri(historicalDefinition.DocumentUrl),token);
        if(historicalDocument.StatusCode!=200)return new("RetryDeferred","equityHistory=DocumentUnavailable",TimeSpan.FromMinutes(30));
        try{await workspace.Equity.ImportHistoricalEquityDocumentAsync(historicalDefinition.Id,historicalDocument.Content,request.Reviewer,request.Reason,historicalId,token);}
        catch(InvalidDataException){return new("Rejected","equityHistory=EvidenceRejected");}
        return BackgroundJobResult.Success();
    }

    [NationalTeamJob("equity-disclosure-sync","直接持股披露检查","0 45 23 * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=4)]
    private async Task<BackgroundJobResult> ExecuteEquityDisclosureSync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(!(request.Operation is "sync" or "equity-sync"))throw new InvalidDataException("任务与业务参数不匹配。");
        var equityIssues=0;var equityRows=0;
        foreach(var security in (await workspace!.Equity.EquityWatchlistAsync(token)).Where(x=>x.Active))
        {
            try
            {
                var sourceUri=OfficialEquitySource.Request(security.Market,security.Ticker);var sourceResponse=await http!.GetAsync(sourceUri,token);
                if(sourceResponse.StatusCode!=200){equityIssues++;continue;}
                await workspace.Equity.SaveEquityDisclosureAsync(security.Market,security.Ticker,sourceUri,sourceResponse.Content,token);equityRows++;
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception error) when(error is InvalidDataException or HttpRequestException or TimeoutException or InvalidOperationException){equityIssues++;}
        }
        return new(equityIssues>0?"RetryDeferred":equityRows==0?"NotPublished":"Succeeded",$"equitySources={equityRows};sourceIssues={equityIssues}",TimeSpan.FromMinutes(30));
    }
}
