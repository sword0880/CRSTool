using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>新事实的持久标记在采集事务中落库；后台恢复后可重算，事件与通知另在同一事务提交。</summary>
    public ExitAlertOverview EvaluateExitAlerts(bool force=false)
    {
        var configuration=AlertConfiguration();var now=clock.GetUtcNow();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now,"China Standard Time").DateTime);
        var last=Scalar("SELECT at FROM nt_alert_evaluation WHERE id=1") as string;
        if(!force&&Scalar("SELECT changed_at FROM nt_alert_pending WHERE id=1") is null&&last is not null&&DateTimeOffset.Parse(last,CultureInfo.InvariantCulture)>now.AddMinutes(-10))return AlertOverview();
        var acceptance=Acceptance(today.AddDays(-1));var gate=acceptance.Count>0&&acceptance.All(x=>x.DataAcceptancePassed);
        var active=Configuration().Where(x=>x.Active).ToArray();var facts=ReadEvidence();var metadata=AlertMetadata();
        var inputs=new List<(AlertFundInput Input,AlertWindows Window)>();var rules=configuration.Rules;
        foreach(var item in active)
        {
            var key=new EtfKey(item.Market,item.Ticker);var calendar=ReadCalendar(key.Market);
            // 只评价已经结束的交易日；当日取得的候选或暂时份额不能冒充日终信号。
            var points=ReadPoints(key).Where(x=>x.Date<today).ToList();
            var date=points.LastOrDefault()?.Date??calendar.LastOrDefault(x=>x.Date<today).Date;
            if(date==default)date=today.AddDays(-1);
            var expected=calendar.LastOrDefault(x=>x.Date<today).Date;var current=date<expected?expected:date;
            var verified=acceptance.SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker)?.CalendarVerified==true;
            // 日历中的损坏日期不能被删掉后变成跨缺口的“相邻”窗口。
            var input=new AlertFundInput(key,item.Name,calendar.Select(x=>x.Date).ToArray(),points,metadata.SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker),verified&&calendar.Where(x=>x.Date<=current).All(x=>x.Valid),now);
            var holdings=NamedEtfHoldings(key);
            var window=ExitAlertEvaluator.Windows(input,current,rules);inputs.Add((input,window));
            ApplyAlertFinding(ExitAlertEvaluator.Single(input,current,rules,gate),configuration);
            EvaluateQuality(key,current,facts,configuration,input.CalendarVerified,input.Metadata is {OriginalConfirmed:false}||holdings.Any(x=>!x.OriginalConfirmed));
            EvaluateMinimum(input,current,configuration,gate,now,holdings);
        }
        // 集群只使用同一交易日的同一窗口；不混用日期，也不相加不同ETF份额。
        if(inputs.Count>0)
        {
            var date=inputs.Max(x=>x.Window.Date);
            var sameDate=inputs.Select(x=>x.Window.Date==date?x:(x.Input,x.Window with{State="IncompleteFacts",Pct5=null,Pct20=null,Triggered=false})).ToArray();
            foreach(var window in new[]{5,20})ApplyAlertFinding(ExitAlertEvaluator.Cluster(sameDate,date,rules,gate,window),configuration);
        }
        RecheckHistoricalAlertInputs(configuration);
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("DELETE FROM nt_alert_pending WHERE id=1");Execute("INSERT INTO nt_alert_evaluation VALUES(1,$at) ON CONFLICT(id) DO UPDATE SET at=excluded.at",("$at",Now));transaction.Commit();
        return AlertOverview();
    }
    private void EvaluateQuality(EtfKey key,DateOnly date,IReadOnlyList<CRS.Plugins.NationalTeamWatch.Application.EvidenceRow> facts,ExitAlertConfiguration configuration,bool calendarVerified,bool proofDamaged)
    {
        var pending=facts.Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker&&x.State is "NeedsReview" or "Critical").ToArray();
        var ids=pending.Select(x=>x.RevisionId).Order().ToArray();var scope=key.Market+":"+key.Ticker;
        var explanation=pending.Length==0?"原先异常候选已通过明确审核或被处理；不据此推断市场风险已解除。"
            :"检测到异常数据，待核实："+string.Join("、",pending.Take(10).Select(x=>x.TradeDate+" "+x.State))+"。不进入正式流出统计或机构减持证明。";
        if(proofDamaged)explanation+=" ETF可比性或具名持仓原件完整性异常，不能继承旧确认；请核对原件登记及SHA256。";
        ApplyAlertFinding(new("DataQualityAnomaly",scope,AlertLane.DataQuality,AlertSeverity.Watch,AlertEvidenceKind.PendingQualityReview,date,
            key.Ticker+" · 数据核查",explanation,ExitAlertEvaluator.Hash(string.Join('|',ids)+"|"+proofDamaged),ids,pending.Length>0||proofDamaged,true,false),configuration);
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.GetUtcNow(),"China Standard Time").DateTime);
        var latest=CollectionAttempts(key,today.AddDays(-7),today).FirstOrDefault(x=>x.State is not ("Expected" or "Fetching" or "RawSaved" or "Parsed" or "Validating"));
        var failed=latest?.State is "AccessBlocked" or "ParseFailed" or "SourceFailed" or "Interrupted"
            ||latest is {State:"Missing" or "NotPublished"}&&latest.Date<today&&IsReviewedTradingDay(key.Market,latest.Date)==true;
        var missing=!ReadPoints(key).Any(x=>x.Date==date)||!calendarVerified;
        ApplyAlertFinding(new("CollectionHealth",scope,AlertLane.DataQuality,AlertSeverity.Watch,AlertEvidenceKind.InsufficientEvidence,date,
            key.Ticker+" · 来源与覆盖状态",failed?$"{latest!.Date:yyyy-MM-dd}最新采集状态{latest.StatusText}，记录于{latest.UpdatedAt}；已有事实保留，风险不能因此变为解除。":missing?"交易日历尚未完整核验或当前应有交易日缺少有效份额，风险状态未知。":"当前应有交易日已具备有效份额，来源状态恢复。",
            ExitAlertEvaluator.Hash((latest?.State??"NoAttempt")+"|"+date+"|"+missing),[],failed||missing,true,false),configuration);
    }
    private void EvaluateMinimum(AlertFundInput input,DateOnly date,ExitAlertConfiguration configuration,bool gate,DateTimeOffset now,IReadOnlyList<NamedEtfHolding> holdings)
    {
        var available=holdings.Where(x=>x.PublishedAt<=now&&x.FirstSeenAt<=now).ToArray();
        var baseline=available.Length==0?(DateOnly?)null:available.Max(x=>x.HoldingAsOf);
        var selected=available.Where(x=>x.HoldingAsOf==baseline).ToArray();var proof=ExitAlertEvaluator.Minimum(selected,input,date,now);
        var complete=proof.State=="DerivedMinimumReduction";
        var description=complete?$"基准日{proof.BaselineDate:yyyy-MM-dd}：具名持有{proof.BaselineShares:N0}份；{date:yyyy-MM-dd}全市场{proof.CurrentShares:N0}份；至少净减少下界{proof.LowerBound:N0}份。"+proof.Explanation
            +string.Join("；",selected.Select(x=>$"{x.LegalEntityId}/{x.LegalName}，公告{x.PublishedAt:O}，首见{x.FirstSeenAt:O}，{x.DocumentUrl} {x.EvidencePage}")):proof.Explanation;
        ApplyAlertFinding(new("MinimumReduction",input.Key.Market+":"+input.Key.Ticker,AlertLane.InstitutionEvidence,AlertSeverity.Watch,
            complete?AlertEvidenceKind.DerivedMinimumReduction:AlertEvidenceKind.InsufficientEvidence,date,"机构持仓相对披露基准的最低净减少",description,
            ExitAlertEvaluator.Hash(string.Join('|',proof.EvidenceIds)+"|"+proof.LowerBound),proof.EvidenceIds,proof.LowerBound>0,complete,!gate,proof.LowerBound,
            [ExitAlertEvaluator.Snapshot(input,date)],selected),configuration);
    }
    public ExitAlertOverview AlertOverview()
    {
        var config=AlertConfiguration();var active=Configuration().Where(x=>x.Active).ToArray();var metadata=AlertMetadata();
        var latest=active.SelectMany(x=>ReadPoints(new(x.Market,x.Ticker))).Select(x=>(DateOnly?)x.Date).Max();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.GetUtcNow(),"China Standard Time").DateTime);
        var acceptance=Acceptance(today.AddDays(-1));
        var pending=Convert.ToInt32(Scalar("SELECT count(*) FROM share_observation_revision WHERE status='NeedsReview'"));
        var events=Convert.ToInt32(Scalar("SELECT count(*) FROM nt_risk_event WHERE state IN ('Active','PendingVerification')"));
        return new(active.Length,metadata.Count(x=>x.OriginalConfirmed),latest,acceptance.Count>0&&acceptance.All(x=>x.DataAcceptancePassed),pending,events,config.Rules.Version);
    }
}
