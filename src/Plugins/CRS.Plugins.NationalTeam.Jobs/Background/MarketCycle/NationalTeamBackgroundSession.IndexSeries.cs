using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;

namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    private sealed class IndexSourceContractException(Uri uri,Exception error):Exception("指数官网口径待核对。",error){public Uri SourceUri {get;}=uri;}
    /// <summary>按已核交易日只补缺口；每段以根请求/代码/区间派生稳定子命令，源站退避不重复已经提交的段。</summary>
    private async Task<BackgroundJobResult> SyncIndexSeriesAsync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        if(commandId is null||request.Operation!="index-series-sync"||request.Market is not null||request.From is not {} from||request.To is not {} through||from>through||through>today||from<new DateOnly(2003,1,1)||through.DayNumber-from.DayNumber>400||request.Ticker is not null&&!IndexResearchCatalog.Definitions.ContainsKey(request.Ticker))return new("Rejected","index=InvalidRangeOrCatalog");
        var definitions=request.Ticker is {} code?new[]{IndexResearchCatalog.Get(code)}:IndexResearchCatalog.Definitions.Values.OrderBy(x=>x.Code).ToArray();
        var methodAndUnitsTargets=new[]{OfficialIndexSeriesSource.UnitsSource}
            .Concat(definitions.Select(x=>new Uri(x.MethodUrl))).Distinct().ToArray();
        var priceTargets=definitions.Select(x=>OfficialIndexSeriesSource.PriceSource(x.Code,from,through)).ToArray();
        if(!IndexSourceTargetsAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,methodAndUnitsTargets)||
           !IndexSourceTargetsAdmitted("cnindex-published-prices-hq",MarketCycleSourceOperation.HistoricalBackfill,priceTargets))
            return new("Rejected","index=SourceApprovalRequired;http=0");
        var calendar=await workspace!.Index.IndexSeriesCalendarAsync(from,through,token);
        var natural=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToArray();
        if(natural.Any(d=>!calendar.Days.TryGetValue(d,out var open)||!open.HasValue))return new("Rejected","index=VerifiedCalendarRequired;reference=SZSE");
        var days=natural.Where(d=>calendar.Days[d]==true).ToArray();if(days.Length==0)return new("Succeeded","index=ReferenceCalendarClosed;http=0;facts=0");
        var initial=await workspace.Index.IndexSeriesRevisionsAsync(from,through,token);var active=initial.Where(x=>x.Value.Kind=="Price"&&definitions.Any(d=>d.Code==x.Value.Code)&&x.State is "Accepted" or "NeedsReview").ToArray();
        if(active.Any(x=>!x.EvidenceHealthy))return new("Rejected","index=ExistingEvidenceUnhealthy");
        var methods=new Dictionary<string,CapturedIndexDocument>(StringComparer.Ordinal);CapturedIndexDocument? units=null;var added=0;var reused=0;
        try
        {
            foreach(var definition in definitions)
            {
                var prior=active.Where(x=>x.Value.Code==definition.Code).ToArray();
                if(days.Any(day=>prior.Count(x=>x.Value.Date==day&&x.State=="Accepted")>1))return new("Rejected","index=MultipleAcceptedVersions");
                var missing=new List<DateOnly>();
                foreach(var day in days)
                {
                    if(prior.Any(x=>x.Value.Date==day&&x.State=="NeedsReview"))continue;
                    if(!request.RefreshSource&&prior.Any(x=>x.Value.Date==day&&x.State=="Accepted")){reused++;continue;}missing.Add(day);
                }
                // 按核验日历的连续缺口分段，不为一个缺日重取全部一年。
                var segments=new List<List<DateOnly>>();foreach(var day in missing)
                {
                    if(segments.Count==0||Array.IndexOf(days,day)!=Array.IndexOf(days,segments[^1][^1])+1)segments.Add([]);segments[^1].Add(day);
                }
                foreach(var segment in segments)
                {
                    token.ThrowIfCancellationRequested();var start=segment[0];var end=segment[^1];var id=IndexChildCommand(commandId.Value,definition.Code,start,end);
                    var outcome=await workspace.Index.IndexSeriesReceiptAsync(id,token);var replay=outcome is not null;
                    if(outcome is not null&&(outcome.Kind!="Price"||outcome.Code!=definition.Code||outcome.From!=start||outcome.Through!=end))return new("Rejected","index=ReceiptScopeMismatch");
                    if(outcome is null)
                    {
                        var methodUri=new Uri(definition.MethodUrl);
                        if(!IndexSourceAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,methodUri))
                            return new("Rejected",$"index=SourceApprovalRequiredBeforeMethod;added={added};reused={reused}");
                        if(!methods.TryGetValue(definition.MethodUrl,out var method)){method=await OfficialIndexSeriesSource.FetchAsync(http!,"Method",methodUri,token);methods.Add(definition.MethodUrl,method);}
                        if(units is null)
                        {
                            if(!IndexSourceAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,OfficialIndexSeriesSource.UnitsSource))
                                return new("Rejected",$"index=SourceApprovalRequiredBeforeUnits;added={added};reused={reused}");
                            units=await OfficialIndexSeriesSource.FetchAsync(http!,"Units",OfficialIndexSeriesSource.UnitsSource,token);
                        }
                        var priceUri=OfficialIndexSeriesSource.PriceSource(definition.Code,start,end);
                        if(!IndexSourceAdmitted("cnindex-published-prices-hq",MarketCycleSourceOperation.HistoricalBackfill,priceUri))
                            return new("Rejected",$"index=SourceApprovalRequiredBeforePrice;added={added};reused={reused}");
                        await ReportAsync(new("DayStarted",start));var document=await OfficialIndexSeriesSource.FetchAsync(http!,"Price",priceUri,token);
                        var packet=new CapturedIndexPacket("Price",definition.Code,start,end,[method,units,document]);var parsed=ParseIndexPacket(packet);
                        if(parsed.Count==0)return new("RetryDeferred",$"index=SourceNotPublished;code={definition.Code};from={start};through={end};added={added}",IndexRetryDelay());
                        if(!parsed.Select(x=>x.Date).SequenceEqual(segment))throw new IndexSourceContractException(new(document.Url),new InvalidDataException("指数日线与核验交易日集合不一致。"));
                        // 长批次中审批可能到期或被撤回；原件取得不等于仍有持久化许可。
                        if(!IndexSourceAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,new(method.Url))||
                           !IndexSourceAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,new(units.Url))||
                           !IndexSourceAdmitted("cnindex-published-prices-hq",MarketCycleSourceOperation.HistoricalBackfill,new(document.Url)))
                            return new("Rejected",$"index=SourceApprovalRequiredBeforeCommit;added={added};reused={reused}");
                        outcome=await workspace.Index.ImportIndexSeriesAsync(packet,token,id);
                    }
                    var fresh=replay?0:outcome.Added;added+=fresh;if(replay)reused+=outcome.Added+outcome.Duplicates;
                    await ReportAsync(new("DayCommitted",end,Received:replay?0:outcome.Added+outcome.Duplicates,Added:fresh,Duplicates:replay?0:outcome.Duplicates,Pending:outcome.Pending){Reused=replay?outcome.Added+outcome.Duplicates:0});
                }
            }
            var final=(await workspace.Index.IndexSeriesRevisionsAsync(from,through,token)).Where(x=>x.Value.Kind=="Price"&&definitions.Any(d=>d.Code==x.Value.Code)).ToArray();
            if(final.Any(x=>(x.State is "Accepted" or "NeedsReview")&&!x.EvidenceHealthy))return new("Rejected","index=EvidenceUnhealthyAfterCommit");
            var pending=final.Count(x=>x.State=="NeedsReview");var accepted=definitions.Sum(d=>days.Count(day=>!final.Any(x=>x.Value.Code==d.Code&&x.Value.Date==day&&x.State=="NeedsReview")&&final.Count(x=>x.Value.Code==d.Code&&x.Value.Date==day&&x.State=="Accepted"&&x.EvidenceHealthy)==1));
            return new(pending>0?"NeedsReview":accepted==days.Length*definitions.Length?"Succeeded":"RetryDeferred",$"indexPriceFacts={accepted};expected={days.Length*definitions.Length};added={added};reused={reused};pending={pending};referenceCalendar=SZSE;timing=NotProven",pending==0&&accepted<days.Length*definitions.Length?IndexRetryDelay():null);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(IndexSourceContractException error){if(http is IOfficialSourceValidationFeedback feedback)feedback.RejectContract(error.SourceUri);return new("NeedsReview",$"index=SourceContractOrMethodReviewRequired;added={added};reused={reused}");}
        catch(InvalidDataException){return new("Rejected",$"index=LocalEvidenceReceiptOrClockInvalid;added={added};reused={reused}");}
        catch(Exception error) when(error is HttpRequestException or TimeoutException or SourceBudgetUnavailableException or InvalidOperationException)
        {return new("RetryDeferred",$"index=SourceUnavailable;added={added};reused={reused}",IndexRetryDelay((error as SourceBudgetUnavailableException)?.Until));}
    }
    /// <summary>当前PB没有历史查询参数；目标日必须与日期头一致，前后两次日期头及同日价格证明同事务绑定。</summary>
    private async Task<BackgroundJobResult> SyncIndexValuationAsync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        if(commandId is null||request.Operation!="index-valuation-sync"||request.Market is not null||request.Ticker is not null||request.To is not {} date||date>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime))return new("Rejected","indexValuation=ExplicitDateRequired");
        var valuationMethodUri=new Uri(IndexResearchCatalog.Get("399381").MethodUrl);
        var valuationTargets=new[]{OfficialIndexSeriesSource.DaySource,valuationMethodUri,OfficialIndexSeriesSource.ValuationSource};
        if(!IndexSourceTargetsAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,valuationTargets))
            return new("Rejected","indexValuation=SourceApprovalRequired;http=0");
        try
        {
            if(await workspace!.Index.IndexSeriesReceiptAsync(commandId.Value,token) is {} prior)
            {
                if(prior.Kind!="Valuation"||prior.Code is not null||prior.From!=date||prior.Through!=date)return new("Rejected","indexValuation=ReceiptScopeMismatch");
                var current=(await workspace.Index.IndexSeriesRevisionsAsync(date,date,token)).Where(x=>x.Value.Kind=="Valuation"&&x.State is "Accepted" or "NeedsReview").ToArray();
                if(current.Any(x=>!x.EvidenceHealthy)||current.Select(x=>x.Value.Code).Distinct().Count()!=10)return new("Rejected","indexValuation=CommittedButCurrentEvidenceUnavailable");
                return new(current.Any(x=>x.State=="NeedsReview")?"NeedsReview":"Succeeded",$"indexValuation=CommittedReceipt;reused={prior.Added+prior.Duplicates};freshAdded=0");
            }
            if(!IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,OfficialIndexSeriesSource.DaySource))
                return new("Rejected","indexValuation=SourceApprovalRequiredBeforeHeader");
            var before=await OfficialIndexSeriesSource.FetchAsync(http!,"HeaderBefore",OfficialIndexSeriesSource.DaySource,token);DateOnly published;
            published=ParseIndexHeader(before);
            if(published!=date)return new("Rejected",$"indexValuation=PublishedDateMismatch;published={published:yyyy-MM-dd};requested={date:yyyy-MM-dd}");
            var prices=await workspace.Index.IndexSeriesRevisionsAsync(date,date,token);
            if(IndexResearchCatalog.Definitions.Values.Where(x=>x.Family=="Cni1000Sector").Any(d=>prices.Count(x=>x.Value.Kind=="Price"&&x.Value.Code==d.Code&&x.State=="Accepted"&&x.EvidenceHealthy)!=1||prices.Any(x=>x.Value.Kind=="Price"&&x.Value.Code==d.Code&&x.State=="NeedsReview")))return new("Rejected","indexValuation=HealthySameDatePricesRequired");
            if(!IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,valuationMethodUri))
                return new("Rejected","indexValuation=SourceApprovalRequiredBeforeMethod");
            var method=await OfficialIndexSeriesSource.FetchAsync(http!,"Method",valuationMethodUri,token);
            if(!IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,OfficialIndexSeriesSource.ValuationSource))
                return new("Rejected","indexValuation=SourceApprovalRequiredBeforeListing");
            var listing=await OfficialIndexSeriesSource.FetchAsync(http!,"Valuation",OfficialIndexSeriesSource.ValuationSource,token);
            if(!IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,OfficialIndexSeriesSource.DaySource))
                return new("Rejected","indexValuation=SourceApprovalRequiredBeforeHeaderAfter");
            var after=await OfficialIndexSeriesSource.FetchAsync(http!,"HeaderAfter",OfficialIndexSeriesSource.DaySource,token);
            if(ParseIndexHeader(after)!=published)return new("RetryDeferred","indexValuation=PublicationDayChangedDuringCapture",IndexRetryDelay());
            var packet=new CapturedIndexPacket("Valuation",null,date,date,[method,before,listing,after]);_=ParseIndexPacket(packet);
            if(!IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,new(method.Url))||
               !IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,new(before.Url))||
               !IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,new(listing.Url))||
               !IndexSourceAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,new(after.Url)))
                return new("Rejected","indexValuation=SourceApprovalRequiredBeforeCommit");
            var result=await workspace.Index.ImportIndexSeriesAsync(packet,token,commandId);
            await ReportAsync(new("DayCommitted",date,Received:result.Added+result.Duplicates,Added:result.Added,Duplicates:result.Duplicates,Pending:result.Pending));
            return new(result.Pending>0?"NeedsReview":"Succeeded",$"indexValuationDate={date:yyyy-MM-dd};added={result.Added};duplicates={result.Duplicates};pending={result.Pending};coverage=Unknown;opportunity=NotEvaluated");
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(IndexSourceContractException error){if(http is IOfficialSourceValidationFeedback feedback)feedback.RejectContract(error.SourceUri);return new("NeedsReview","indexValuation=SourceContractOrMethodReviewRequired");}
        catch(InvalidDataException){return new("Rejected","indexValuation=LocalEvidencePriceReferenceOrClockInvalid");}
        catch(Exception error) when(error is HttpRequestException or TimeoutException or SourceBudgetUnavailableException or InvalidOperationException)
        {return new("RetryDeferred","indexValuation=SourceUnavailable",IndexRetryDelay((error as SourceBudgetUnavailableException)?.Until));}
    }
    /// <summary>每个实际请求都绑定到宿主审批台账中的精确来源基址；所有端点先整体预检，缺任一许可则不发起HTTP。</summary>
    private bool IndexSourceTargetsAdmitted(string dataset,MarketCycleSourceOperation operation,IEnumerable<Uri> targets)
        =>targets.All(target=>IndexSourceAdmitted(dataset,operation,target));

    private bool IndexSourceAdmitted(string dataset,MarketCycleSourceOperation operation,Uri target)
    {
        if(indexSourceApprovals?.Find(dataset) is not {} approval)return false;
        var (expectedSource,expectedBase)=dataset switch
        {
            "cnindex-published-prices" => ("CNINDEX",new Uri("https://www.cnindex.com.cn/")),
            "cnindex-published-prices-hq" => ("CNINDEX-HQ",new Uri("https://hq.cnindex.com.cn/")),
            "cnindex-published-valuation" => ("CNINDEX",new Uri("https://www.cnindex.com.cn/")),
            _ => (string.Empty,new Uri("https://invalid.example/"))
        };
        if(expectedSource.Length==0||approval.SourceId!=expectedSource||approval.SourceUri!=expectedBase||
           approval.DatasetId!=dataset||approval.ContractVersion!="cnindex-index-series-v1"||
           approval.ParserVersion!=OfficialIndexSeriesSource.ParserVersion)return false;
        try
        {
            MarketCycleSourceAdmission.RequireTarget(approval,expectedSource,dataset,target,operation,DateTimeOffset.UtcNow,indexSourceApprovals);
            return true;
        }
        catch(Exception error) when(error is InvalidDataException or InvalidOperationException or ArgumentException){return false;}
    }
    /// <summary>报告页显示的是完整采集路径是否具备准入，而不是只检查某一条数据集记录。</summary>
    private bool IndexPriceCollectionAllowed()
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        var methodAndUnits=new[]{OfficialIndexSeriesSource.UnitsSource}
            .Concat(IndexResearchCatalog.Definitions.Values.Select(x=>new Uri(x.MethodUrl))).Distinct().ToArray();
        return IndexSourceTargetsAdmitted("cnindex-published-prices",MarketCycleSourceOperation.HistoricalBackfill,methodAndUnits)&&
               IndexSourceAdmitted("cnindex-published-prices-hq",MarketCycleSourceOperation.HistoricalBackfill,
                   OfficialIndexSeriesSource.PriceSource("399317",today,today));
    }

    private bool IndexValuationCollectionAllowed()
    {
        var targets=new[]{OfficialIndexSeriesSource.DaySource,OfficialIndexSeriesSource.ValuationSource,
            new Uri(IndexResearchCatalog.Get("399381").MethodUrl)};
        return IndexSourceTargetsAdmitted("cnindex-published-valuation",MarketCycleSourceOperation.AutomatedPolling,targets);
    }

    // 前后日期头均属同一来源合同；畸形后置头不能漏出为宿主故障或误记为本地证据损坏。
    private static DateOnly ParseIndexHeader(CapturedIndexDocument document)
    {
        try{return OfficialIndexSeriesSource.HeaderDate(document);}
        catch(Exception error) when(error is InvalidDataException or JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException){throw new IndexSourceContractException(OfficialIndexSeriesSource.DaySource,error);}
    }
    private IReadOnlyList<IndexSeriesValue> ParseIndexPacket(CapturedIndexPacket packet)
    {
        var now=DateTimeOffset.UtcNow;
        if(packet.Documents.Any(x=>x.RetrievedAt>now||Convert.ToHexString(SHA256.HashData(x.Content))!=x.Sha256))throw new InvalidDataException("本地捕获时钟或缓冲区身份改变。");
        try{return OfficialIndexSeriesSource.Parse(packet,now);}catch(InvalidDataException error){throw new IndexSourceContractException(packet.Kind=="Price"?OfficialIndexSeriesSource.PriceSource(packet.Code!,packet.From,packet.Through):OfficialIndexSeriesSource.ValuationSource,error);}
    }
    private TimeSpan IndexRetryDelay(DateTimeOffset? until=null)
    {
        var now=CRS.Timing.BeijingClock.Now;var retry=now.AddMinutes(30);var local=(http as OfficialHttpGateway)?.RetryAt(OfficialIndexSeriesSource.ValuationSource);if(local>retry)retry=local.Value;if(until>retry)retry=until.Value;return retry-now;
    }
    private static Guid IndexChildCommand(Guid root,string code,DateOnly from,DateOnly through)=>new(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Version="index-range-child-v1",root,code,from,through})).AsSpan(0,16));
}
