using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

public sealed partial class NationalTeamBackgroundSession
{
    /// <summary>仅管理员持久手动作业；缺日历不猜工作日，分日提交，恢复时按根请求和日期读取原回执。</summary>
    private async Task<BackgroundJobResult> SyncIndustryPeAsync(EtfRequest request,CancellationToken token,Guid? commandId)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        if(request.Operation!="industry-pe-sync"||commandId is null||request.From is not {} from||request.To is not {} through
            ||from>through||from<new DateOnly(2021,4,1)||through>today||through.DayNumber-from.DayNumber>400
            ||request.Market is not null||request.Ticker is not null||request.Revision is not null||request.Reviewer is not null)
            return new("Rejected","industry=InvalidRangeOrIdentity");
        var calendar=await workspace!.IndustryPeCalendarAsync(from,through,token);
        if(calendar.Days.Values.Any(x=>!x.HasValue))return new("Rejected","industry=ReviewedCalendarRequired");
        var prior=await workspace.IndustryPeRevisionsAsync(from,through,token);var reused=0;var added=0;var pending=0;var dates=calendar.Days.Where(x=>x.Value==true).Select(x=>x.Key).Order().ToArray();
        foreach(var date in dates)
        {
            token.ThrowIfCancellationRequested();
            var current=prior.Where(x=>x.Date==date).ToArray();
            if(!request.RefreshSource&&current.GroupBy(x=>x.Values.Code).Count()==11&&current.All(x=>x.EvidenceHealthy))
            {pending+=current.Count(x=>x.State=="NeedsReview");reused++;continue;}
            // 子命令身份确定且跨进程稳定；部分日期提交后重试，不重取已经有完整回执的原件。
            var dayId=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(commandId.Value.ToString("N")+"|industry-pe|"+date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture))).AsSpan(0,16));
            try
            {
                var outcome=await workspace.IndustryPeReceiptAsync(dayId,token);
                var receiptReused=outcome is not null;
                if(outcome is null)
                {
                    await ReportAsync(new("DayStarted",date));
                    CapturedIndustryPeDocument document;
                    try
                    {
                        document=await OfficialIndustryPeSource.FetchAsync(http!,date,token);
                        _=OfficialIndustryPeSource.Parse(document,DateTimeOffset.UtcNow);
                    }
                    catch(InvalidDataException)
                    {
                        if(http is IOfficialSourceValidationFeedback feedback)feedback.RejectContract(OfficialIndustryPeSource.Source(date));
                        return new("RetryDeferred",$"industry=SourceContractUnavailable;date={date:yyyy-MM-dd};added={added};reusedDays={reused}",IndustryRetryDelay(date));
                    }
                    outcome=await workspace.ImportIndustryPeAsync(document,token,dayId);
                }
                else reused++;
                // 重放旧回执只表示先前日期已提交，不能制造本次新增/收取计数。
                var freshAdded=receiptReused?0:outcome.Added;added+=freshAdded;pending+=outcome.Pending;
                await ReportAsync(new("DayCommitted",date,Received:receiptReused?0:outcome.Added+outcome.Duplicates,Added:freshAdded,Duplicates:receiptReused?0:outcome.Duplicates,Pending:outcome.Pending){Reused=receiptReused?11:0});
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(InvalidDataException)
            {
                // 已核对来源之后的本地证据、时钟或回执故障不能归责源站，也不能清空后重取。
                return new("Rejected",$"industry=LocalEvidenceOrReceiptInvalid;date={date:yyyy-MM-dd};added={added};reusedDays={reused}");
            }
            catch(Exception error) when(error is HttpRequestException or TimeoutException or InvalidOperationException or SourceBudgetUnavailableException)
            {
                // 解析失败与HTTP失败均保留原ID，按持久来源冷却等待；不跳过失败日后宣称区间完成。
                return new("RetryDeferred",$"industry=SourceOrContractUnavailable;date={date:yyyy-MM-dd};added={added};reusedDays={reused}",IndustryRetryDelay(date,(error as SourceBudgetUnavailableException)?.Until));
            }
        }
        var final=await workspace.IndustryPeRevisionsAsync(from,through,token);
        if(final.Any(x=>(x.State is "Accepted" or "NeedsReview")&&!x.EvidenceHealthy))return new("Rejected","industry=EvidenceUnhealthy");
        pending=final.Count(x=>x.State=="NeedsReview");
        return new(pending>0?"NeedsReview":"Succeeded",$"industryDays={dates.Length};added={added};reusedDays={reused};pending={pending};opportunity=NotEvaluated");
    }
    /// <summary>宿主持久重试不得早于源站实际冷却；本地30分钟仅是没有更晚Until时的最小退避。</summary>
    private TimeSpan IndustryRetryDelay(DateOnly date,DateTimeOffset? until=null)
    {
        var now=CRS.Timing.BeijingClock.Now;var retry=now.AddMinutes(30);var local=(http as OfficialHttpGateway)?.RetryAt(OfficialIndustryPeSource.Source(date));
        if(local>retry)retry=local.Value;if(until>retry)retry=until.Value;return retry-now;
    }
}
