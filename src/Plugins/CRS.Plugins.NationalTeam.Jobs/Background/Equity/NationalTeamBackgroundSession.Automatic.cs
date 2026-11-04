using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    [NationalTeamQuery("equity-auto-status",NationalTeamQueryKind.ShortRead)]
    private async Task<NationalTeamQueryResult> QueryAutomaticEquity(EtfRequest request,CancellationToken token)
        =>new([new NationalTeamQuerySerialization(token).Row(await workspace!.Equity.AutomaticEquityStatusAsync(token))],null);

    [NationalTeamJob("equity-auto-sync","机构直接持股自动发现与更新","0 15 0 * * ?","China Standard Time",true,true,Group="collection",MaximumConcurrency=2,Order=11)]
    private async Task<BackgroundJobResult> ExecuteAutomaticEquity(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        var current=AutomaticEquitySource.SecuritiesUri;
        try
        {
            if(http is not IPluginJsonHttpGateway jsonHttp)return new("Rejected","automaticEquity=SourceCapabilityMissing");
            var status=await workspace!.Equity.AutomaticEquityStatusAsync(token);var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
            if(status.SecuritiesCheckedAt is not {} checkedAt||DateOnly.FromDateTime(checkedAt.DateTime)<today)
            {
                var securities=await http.GetAsync(current,token);if(securities.StatusCode!=200)return AutomaticSourceWait(current,"SecurityDirectoryUnavailable");
                try{await workspace.Equity.SaveAutomaticEquitySecuritiesAsync(securities.Content,token);}
                catch(Exception error) when(AutomaticContractError(error)){return await AutomaticContractFailure(current,securities.Content,token);}
            }
            await workspace.Equity.BeginAutomaticEquityWindowAsync(today.AddDays(-1),token);
            var indexed=0;var handled=0;var verified=0;
            // 每个执行切片有明确上限；有积压时沿宿主期限继续，取消不丢水位，不占满其它来源。
            while(handled<10&&indexed<3)
            {
                token.ThrowIfCancellationRequested();
                var documents=await workspace.Equity.PendingAutomaticEquityDocumentsAsync(1,token);
                if(documents.Count>0)
                {
                    var document=documents[0];current=new(document.Url);
                    if(document.DeclaredKilobytes>8*1024)
                    {await workspace.Equity.RecordAutomaticEquityIssueAsync(document.Id,document.Ticker+" 原件超出自动读取大小上限，保留目录转异常",null,token);handled++;continue;}
                    PublicHttpResponse original;
                    try{original=await http.GetAsync(current,token);}
                    catch(InvalidDataException)
                    {await workspace.Equity.RecordAutomaticEquityIssueAsync(document.Id,document.Ticker+" 原件未通过有界读取，保留目录转异常",null,token);handled++;continue;}
                    if(original.StatusCode!=200)return AutomaticSourceWait(current,"OriginalUnavailable");
                    if(original.Content.Length<5||!original.Content.AsSpan(0,5).SequenceEqual("%PDF-"u8))
                        return await AutomaticContractFailure(current,original.Content,token);
                    var result=await workspace.Equity.ImportAutomaticEquityDocumentAsync(document.Id,original.Content,token);
                    handled++;if(result=="Verified")verified++;
                    await ReportAsync(new("AutomaticEquityDocument",Date:document.ReportAsOf,Market:document.Market,Received:handled,Added:verified));
                    continue;
                }
                status=await workspace.Equity.AutomaticEquityStatusAsync(token);
                if(status.Cursor is not {Complete:false,Date:{} day} cursor)break;
                current=AutomaticEquitySource.ReportsUri;
                var page=await jsonHttp.PostJsonAsync(current,AutomaticEquitySource.Request(day,cursor.Page),token);
                if(page.StatusCode!=200)return AutomaticSourceWait(current,"ReportDirectoryUnavailable");
                try{await workspace.Equity.SaveAutomaticEquityPageAsync(page.Content,token);}
                catch(Exception error) when(AutomaticContractError(error)){return await AutomaticContractFailure(current,page.Content,token);}
                indexed++;
            }
            status=await workspace.Equity.AutomaticEquityStatusAsync(token);
            var checkpoint=$"automaticEquity:indexPages={indexed};handled={handled};verified={verified};pending={status.Pending};exceptions={status.NeedsReview};discoveryComplete={status.Cursor?.Complete==true}";
            return status.Pending>0||status.Cursor is{Complete:false}?new("RetryDeferred",checkpoint,TimeSpan.FromSeconds(30)):new("Succeeded",checkpoint);
        }
        catch(SourceBudgetUnavailableException error){return new("RetryDeferred","automaticEquity=SourceWait;"+error.Code,AutomaticDelay(current,error.Until));}
        catch(Exception error) when(error is HttpRequestException or TimeoutException)
        {return AutomaticSourceWait(current,"SourceUnavailable");}
    }
    private static bool AutomaticContractError(Exception error)=>error is InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or FormatException or OverflowException;
    private BackgroundJobResult AutomaticSourceWait(Uri source,string reason)=>new("RetryDeferred","automaticEquity="+reason,AutomaticDelay(source,null));
    private TimeSpan AutomaticDelay(Uri source,DateTimeOffset? until)
    {
        var next=(http as OfficialHttpGateway)?.RetryAt(source);
        if(until>next)next=until;
        var delay=next-CRS.Timing.BeijingClock.Now;return delay>TimeSpan.FromSeconds(30)?delay.Value:TimeSpan.FromSeconds(30);
    }
    private async Task<BackgroundJobResult> AutomaticContractFailure(Uri source,byte[] original,CancellationToken token)
    {
        (http as IOfficialSourceValidationFeedback)?.RejectContract(source);
        await workspace!.Equity.RecordAutomaticEquityIssueAsync(null,"官方目录或原文返回格式不符，等待来源恢复；未批准持仓",original,token);
        return AutomaticSourceWait(source,"SourceContractMismatch");
    }
}
