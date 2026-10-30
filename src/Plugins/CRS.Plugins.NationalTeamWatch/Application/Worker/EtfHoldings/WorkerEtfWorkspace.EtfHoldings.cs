// ETF持仓研究客户端，核验公告日期形状与后台能力，目录、原件候选及具名历史分别读取。
using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Serialization;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace
{
    public async Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)
    {
        if(!EtfHoldingPublication.HasValidShape(review.PublishedAt,review.PublicationDate,review.PublicationPrecision))throw new InvalidDataException("请选择准确公告时刻或仅公告日期，不能混填或补造时刻。");
        var operation="alert-holding-review";
        if(review.PublicationPrecision==EtfPublicationPrecision.DateOnly)
        {
            var capabilities=await EnsureCapabilitiesAsync(token);
            if(capabilities is null||!capabilities.Capabilities.Contains(EtfQueryCapabilities.PublicationDateHoldings,StringComparer.Ordinal))
                throw new InvalidDataException("后台未声明公告日期核验能力，请更新后台插件后再保存。");
            operation="alert-holding-review-date-v1";
        }
        await RunAsync("alert-manage",new(operation,NamedEtfHolding:review),token);
    }
    public async Task<EtfHolderCandidateReport> EtfHolderCandidatesAsync(EtfKey key,string documentId,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("etf-holder-candidates",key.Market,key.Ticker,Revision:documentId,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<EtfHolderCandidateReport>(blocks,token);
        if(report.Document is null||report.Holders is null||report.Holders.Any(x=>x is null)||report.Document.Id!=documentId||report.Document.Market!=key.Market||report.Document.Ticker!=key.Ticker
            ||report.ParserVersion is not (EtfHolderParserVersions.Huatai300 or EtfHolderParserVersions.EfundChiNext or EtfHolderParserVersions.HarvestCsi300)
            ||report.ParserVersion==EtfHolderParserVersions.EfundChiNext&&(key.Market!="SZSE"||key.Ticker!="159915")
            ||report.ParserVersion==EtfHolderParserVersions.HarvestCsi300&&(key.Market!="SZSE"||key.Ticker!="159919")
            ||report.State is not ("NeedsReview" or "UnsupportedTemplate" or "NotDisclosed")
            ||report.State=="UnsupportedTemplate"&&(report.Holders.Count!=0||report.ReportAsOf is not null||report.PublicationDate is not null)
            ||report.State!="UnsupportedTemplate"&&(report.ReportAsOf is null||report.PublicationDate is null||report.PhysicalPage is null)
            ||report.State=="NotDisclosed"&&(report.ParserVersion!=EtfHolderParserVersions.EfundChiNext||report.Holders.Count!=0||report.Scope!="AggregateOnlyNoNamedTable"||report.PercentBasis is not null)
            ||report.State=="NeedsReview"&&(report.Holders.Count!=11||!report.Holders.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11))
                ||report.Holders.Any(x=>string.IsNullOrWhiteSpace(x.RegisteredHolder)||x.Shares<=0||x.PercentOfListedUnits is <=0 or >100)
                ||report.ParserVersion==EtfHolderParserVersions.Huatai300&&(key.Market!="SSE"||key.Ticker!="510300")))
            throw new InvalidDataException("持有人候选与所选原件、解析版本或披露状态不符。");
        return report;
    }
    public async Task<NamedEtfHoldingHistoryReport> EtfHoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("etf-holding-history",key.Market,key.Ticker,To:asOf,Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<NamedEtfHoldingHistoryReport>(blocks,token);
        if(report.FormatVersion!=NamedEtfHoldingHistoryReport.Version||report.Input.Key!=key||report.Input.AsOf!=asOf)throw new InvalidDataException("具名ETF历史证券、统计日或版本不符。");
        var rebuilt=NamedEtfHoldingHistory.Evaluate(report.Input);
        if(rebuilt.Fingerprint!=report.Fingerprint)throw new InvalidDataException("具名ETF历史归一输入或结果指纹不符。");return rebuilt;
    }
    private readonly Dictionary<(EtfKey Key,DateOnly From,DateOnly Through),BackgroundRequest> etfReportSearches=[];
    public async Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token)
    {
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("etf-report-index",key.Market,key.Ticker,Count:2),token)).Rows;
        var result=JsonDocumentBlocks.Decode<EtfReportIndexSnapshot>(blocks,token);
        if(result.Key!=key||result.FormatVersion!=EtfReportIndexSnapshot.Version||result.State is not ("NotSearched" or "IndexedUnreviewed" or "EvidenceUnavailable")
            ||result.State=="NotSearched"&&(result.SearchId is not null||result.DeclaredTotal is not null||result.Documents.Count!=0)
            ||result.State!="NotSearched"&&(result.SearchId is null||result.From is null||result.Through is null||result.CapturedAt is null||result.DeclaredTotal!=result.Documents.Count)
            ||result.Documents.Any(x=>!x.SecurityCodes.Contains(key.Ticker)))throw new InvalidDataException("ETF报告目录证券、版本或完整性不符。");
        return result;
    }
    public async Task<EtfReportSearchReceipt> QueueEtfReportsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
    {
        var scope=(key,from,through);
        if(!etfReportSearches.TryGetValue(scope,out var request))
        {
            if(etfReportSearches.Count>=128)throw new InvalidDataException("尚有较多ETF检索请求未核对，请先查询已有请求的结果。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"etf-report-search",Payload:JsonSerializer.Serialize(new EtfRequest("etf-report-search",key.Market,key.Ticker,from,through)));
            etfReportSearches.Add(scope,request);
        }
        var response=await SendAsync(request,token);
        if(response.Code is not ("Accepted" or "Succeeded" or "RetryDeferred")&&response.Receipt is null)
        {etfReportSearches.Remove(scope);throw new InvalidDataException("后台未接受ETF报告检索："+response.Code);}
        if(response.Receipt?.State=="Completed")etfReportSearches.Remove(scope);
        return new(request.RequestId,response.Receipt?.State??response.Code,response.Receipt?.Code??response.Code);
    }
}
