using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>同一只读SQLite快照中组装四类事实，正式调用领域评估器；不联网、不触发提醒。</summary>
internal sealed class MarketCycleStudyStore(IMarketRepositorySession session,IndexSeriesStore indices,
    MarketCalendarStore calendar,MarketActivityStore activity,MarketBreadthStore breadth)
{
    public MarketCycleStudyReport Read(DateOnly asOf,DateTimeOffset? requestedCutoff)
    {
        // 默认知识时刻在建立读取快照前固定；不能用查询途中较晚的时刻描述已固定的较早资料范围。
        var cutoff=requestedCutoff??session.ReadSnapshotCutoff??session.Clock.GetUtcNow();
        if(cutoff==default||cutoff>session.Clock.GetUtcNow()||session.ReadSnapshotCutoff is {} snapshotCutoff&&cutoff>snapshotCutoff||asOf==default||asOf<DateOnly.MinValue.AddDays(400)
            ||asOf>=DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("周期研究须选择知识截止之前的统计日，避免把当日未结束行情当成收盘事实。");
        var from=asOf.AddDays(-400);
        var sse=calendar.CycleCalendar(from,asOf,cutoff,"SSE");var szse=calendar.CycleCalendar(from,asOf,cutoff,"SZSE");
        var verified=sse.Days.All(x=>x.Value.HasValue&&szse.Days.TryGetValue(x.Key,out var other)&&other.HasValue&&x.Value==other);
        var trading=sse.Days.Where(x=>x.Value==true&&szse.Days.GetValueOrDefault(x.Key)==true).Select(x=>x.Key).Order().ToArray();
        var calendarFingerprint=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{Contract="SSE-SZSE-calendar-agreement-v1",Sse=sse.Fingerprint,Szse=szse.Fingerprint,verified,Trading=trading}));
        var prices=indices.CyclePrices(from,asOf,cutoff);var activityReport=activity.MarketActivityKnowledge(from,asOf,cutoff);
        // 广度逐证券档案可能很大，仅核验规则实际依赖的六个交易日窗口；历史档案仍可单独查询。
        var breadthFrom=trading.TakeLast(6).DefaultIfEmpty(asOf).First();
        var breadthReport=breadth.Read(breadthFrom,asOf,cutoff);var sources=new List<MarketCycleSourceReference>(prices.Sources);sources.AddRange(breadthReport.Sources);sources.AddRange(sse.Sources);sources.AddRange(szse.Sources);
        var totals=new List<MarketCycleActivityPoint>();
        foreach(var day in activityReport.Revisions.GroupBy(x=>x.Date).OrderBy(x=>x.Key))
        {
            session.OperationCancellation.ThrowIfCancellationRequested();
            var live=day.Where(x=>x.State!=MarketCycleObservationState.Rejected).OrderBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray();
            foreach(var row in live)sources.Add(new("Activity",row.Date,row.Id,"ExchangeOriginal",row.SourceUrl,row.EvidenceHash,row.OriginalHash,row.SourceRetrievedAt,row.FirstSeenAt,row.StateRecordedAt,row.State.ToString()));
            if(live.Length==0)continue;
            // 同一市场同值多份原件只计一次；任何未决、损坏、异值或缺市均不得恢复为可计算的合计。
            var selected=new List<MarketActivityKnowledgeRevision>();var accepted=true;
            foreach(var market in new[]{"SSE","SZSE"})
            {
                var group=live.Where(x=>x.Market==market).ToArray();
                if(group.Length==0||group.Any(x=>x.State!=MarketCycleObservationState.Accepted)||group.Select(x=>(x.AmountYuan,x.VolumeShares)).Distinct().Count()!=1){accepted=false;continue;}
                selected.Add(group[0]);
            }
            var fingerprint=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{Contract="SSE-SZSE-activity-derived-v1",Date=day.Key,Revisions=live.Select(x=>new{x.Id,x.RegistrationHash,x.StateEventHash,x.AmountYuan,x.VolumeShares,x.State})}));
            totals.Add(new(day.Key,accepted?selected.Sum(x=>x.AmountYuan):null,accepted?selected.Sum(x=>x.VolumeShares):null,null,
                live.Max(x=>x.StateRecordedAt>x.FirstSeenAt?x.StateRecordedAt:x.FirstSeenAt),live.Max(x=>x.SourceRetrievedAt),fingerprint,fingerprint,
                live.Any(x=>x.State==MarketCycleObservationState.Critical)?MarketCycleObservationState.Critical:accepted?MarketCycleObservationState.Accepted:MarketCycleObservationState.NeedsReview));
        }
        var input=new MarketCycleInput("SSE+SZSE","399317",asOf,cutoff,verified,trading,prices.Points,totals,breadthReport.Points,"SSE-SZSE-calendar-agreement-v1",calendarFingerprint);
        return MarketCycleStudyReport.Freeze(input,sources,activityReport.Fingerprint);
    }
}
