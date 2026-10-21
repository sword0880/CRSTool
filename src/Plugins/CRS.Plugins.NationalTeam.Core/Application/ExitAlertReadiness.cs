namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>只读数据验收明细：待审与损坏不能当成可自动补采的普通缺日。</summary>
public sealed record AlertReadinessIssue(DateOnly Date,string State,string? RevisionId,string? LastAttemptState,string? LastAttemptAt);

/// <summary>已登记日历内的缺口诊断；250日历史和20日连续采集是两项相互独立的真实门槛。</summary>
public sealed record AlertReadinessDiagnostic(string Market,string Ticker,DateOnly Through,DateOnly? TargetFrom,
    bool CalendarUsable,int RegisteredTradingDays,int CalendarDaysShortfall,int AcceptedDays,
    int MissingDays,int PendingReviewDays,int DamagedDays,int RejectedDays,
    bool History250Passed,bool Continuous20Passed,IReadOnlyList<AlertReadinessIssue> Issues)
{
    public int UnresolvedDays=>Issues.Count;
    public bool DataAcceptancePassed=>History250Passed&&Continuous20Passed;
}

/// <summary>明确区分缺失份额、人工核查、损坏原件和未登记的日历范围；不提交采集、审核或通知。</summary>
public static class ExitAlertReadiness
{
    /// <summary>协议单次覆盖查询最多400自然日；长历史按不重叠相邻区间合并，不跳过缺少日历的中间区间。</summary>
    public static CoverageReport Combine(IReadOnlyList<CoverageReport> segments)
    {
        if(segments.Count is <1 or >20)throw new InvalidDataException("验收覆盖分段数量无效。");
        var first=segments[0];var last=segments[^1];
        for(var i=0;i<segments.Count;i++)
        {
            var part=segments[i];
            if(part.Market!=first.Market||part.Ticker!=first.Ticker||part.From>part.To||
                part.To.DayNumber-part.From.DayNumber>400||
                i>0&&part.From.DayNumber!=segments[i-1].To.DayNumber+1||
                part.Days.Count!=part.RegisteredTradingDays||
                part.Days.Any(d=>d.Date<part.From||d.Date>part.To))
                throw new InvalidDataException("覆盖分段越界、重叠、丢失或跨证券；拒绝拼接历史验收。");
        }
        var days=segments.SelectMany(x=>x.Days).OrderBy(x=>x.Date).ToArray();
        if(days.Select(x=>x.Date).Distinct().Count()!=days.Length)
            throw new InvalidDataException("覆盖分段存在重复交易日。");
        var registered=segments.Sum(x=>x.RegisteredTradingDays);
        var accepted=segments.Sum(x=>x.AcceptedDays);
        var calendarVerified=segments.All(x=>x.CalendarVerified);
        var rangeCovered=segments.All(x=>x.CalendarRangeCovered);
        var state=registered==0?"NoCalendar":!calendarVerified?"CalendarCritical":!rangeCovered?"CalendarRangeIncomplete":
            accepted==registered?"CompleteRegisteredCalendar":"Incomplete";
        return new(first.Market,first.Ticker,first.From,last.To,registered,accepted,
            segments.Sum(x=>x.MissingDays),segments.Sum(x=>x.PendingDays),segments.Sum(x=>x.CriticalDays),segments.Sum(x=>x.RejectedDays),
            calendarVerified&&registered>0?decimal.Round(100m*accepted/registered,2):null,
            calendarVerified,rangeCovered,state,days);
    }

    public static AlertReadinessDiagnostic Analyze(EtfAcceptance acceptance,CoverageReport coverage)
    {
        if(acceptance.Market!=coverage.Market||acceptance.Ticker!=coverage.Ticker||
            acceptance.Through!=coverage.To||
            acceptance.TargetFrom is {} start&&start!=coverage.From||
            acceptance.ExpectedDays!=coverage.RegisteredTradingDays||
            acceptance.AcceptedDays!=coverage.AcceptedDays||
            acceptance.UnresolvedDays!=coverage.RegisteredTradingDays-coverage.AcceptedDays||
            coverage.Days.Count!=coverage.RegisteredTradingDays||
            coverage.Days.Select(x=>x.Date).Distinct().Count()!=coverage.Days.Count||
            coverage.Days.Any(x=>x.Date<coverage.From||x.Date>coverage.To)||
            coverage.Days.Count(x=>x.State=="Accepted")!=coverage.AcceptedDays||
            coverage.Days.Any(x=>x.State is not ("Accepted" or "Missing" or "NeedsReview" or "Critical" or "CalendarCritical" or "QualityRejected")))
            throw new InvalidDataException("监控验收与逐日覆盖不一致，可能发生了并发修订；拒绝生成完整缺口报告。");
        var unresolved=coverage.Days.Where(x=>x.State!="Accepted").OrderBy(x=>x.Date)
            .Select(x=>new AlertReadinessIssue(x.Date,x.State,x.RevisionId,x.LastAttempt?.State,x.LastAttempt?.UpdatedAt)).ToArray();
        var usable=acceptance.CalendarVerified&&coverage.CalendarVerified&&coverage.CalendarRangeCovered
            &&acceptance.TargetFrom is not null;
        return new(acceptance.Market,acceptance.Ticker,acceptance.Through,acceptance.TargetFrom,
            usable,coverage.RegisteredTradingDays,Math.Max(0,250-coverage.RegisteredTradingDays),coverage.AcceptedDays,
            unresolved.Count(x=>x.State=="Missing"),unresolved.Count(x=>x.State=="NeedsReview"),
            unresolved.Count(x=>x.State is "Critical" or "CalendarCritical"),unresolved.Count(x=>x.State=="QualityRejected"),
            acceptance.History250Passed,acceptance.Continuous20Passed,unresolved);
    }
}
