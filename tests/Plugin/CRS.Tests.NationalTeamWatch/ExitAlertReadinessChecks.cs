using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>只读验收差异检查：不会用缺失交易日伪造250日历史，或将回填说成20日连续采集。</summary>
internal static class ExitAlertReadinessChecks
{
    private static void Check(bool ok,string label)
    {
        if(!ok)throw new Exception(label);
        Console.WriteLine("PASS 预警准入："+label);
    }
    private static void Reject(Action action,string label)
    {
        try{action();}catch(InvalidDataException){Check(true,label);return;}
        throw new Exception(label);
    }
    public static void Run()
    {
        var from=new DateOnly(2026,1,5);var through=from.AddDays(4);
        var days=new[]
        {
            new CoverageDay(from,"Accepted","r0"),
            new CoverageDay(from.AddDays(1),"Missing",null),
            new CoverageDay(from.AddDays(2),"NeedsReview","r2"),
            new CoverageDay(from.AddDays(3),"Critical","r3"),
            new CoverageDay(through,"Accepted","r4")
        };
        var acceptance=new EtfAcceptance("SSE","510300",through,from,5,2,3,0,true);
        var coverage=new CoverageReport("SSE","510300",from,through,5,2,1,1,1,0,40m,true,true,"Incomplete",days);
        var left=coverage with{To=from.AddDays(2),RegisteredTradingDays=3,AcceptedDays=1,MissingDays=1,
            PendingDays=1,CriticalDays=0,CoveragePercent=33.33m,Days=days[..3]};
        var right=coverage with{From=from.AddDays(3),RegisteredTradingDays=2,AcceptedDays=1,MissingDays=0,
            PendingDays=0,CriticalDays=1,CoveragePercent=50m,Days=days[3..]};
        var merged=ExitAlertReadiness.Combine([left,right]);
        Check(merged.RegisteredTradingDays==5&&merged.AcceptedDays==2&&merged.Days.Count==5
            &&merged.State=="Incomplete","超过单查询长度的覆盖分段能保持状态和日期连续");
        Reject(()=>ExitAlertReadiness.Combine([left,right with{From=from.AddDays(4)}]),"覆盖分段缺一日必须失败关闭");
        Reject(()=>ExitAlertReadiness.Combine([left,right with{Market="SZSE"}]),"分段跨市场不能合并");
        Reject(()=>ExitAlertReadiness.Combine([left,right with{Days=[days[2],days[4]]}]),"分段重复交易日不能拼接");
        var result=ExitAlertReadiness.Analyze(acceptance,merged);
        Check(result.CalendarUsable&&result.CalendarDaysShortfall==245&&result.MissingDays==1&&result.PendingReviewDays==1
            &&result.DamagedDays==1&&result.UnresolvedDays==3,"缺日、待审、损坏分类独立，250日缺口只记日历不足");
        Check(result.Issues[1].RevisionId=="r2"&&result.Issues[2].State=="Critical"
            &&!result.History250Passed&&!result.Continuous20Passed&&!result.DataAcceptancePassed,
            "保留修订证据；历史未达标及连续采集未达标不批准正式预警");
        var unverified=ExitAlertReadiness.Analyze(acceptance with{CalendarVerified=false},coverage);
        Check(!unverified.CalendarUsable&&unverified.Issues.Count==3,"未核验日历不将已登记缺口视作官方完整日历");
        var noCalendar=new CoverageReport("SSE","510300",from,through,0,0,0,0,0,0,null,false,false,"NoCalendar",[]);
        var missingCalendar=ExitAlertReadiness.Analyze(new("SSE","510300",through,null,0,0,0,0,false),noCalendar);
        Check(!missingCalendar.CalendarUsable&&missingCalendar.CalendarDaysShortfall==250&&missingCalendar.Issues.Count==0,
            "无日历不能编造缺失份额的具体交易日");
        Reject(()=>ExitAlertReadiness.Analyze(acceptance with{AcceptedDays=3,UnresolvedDays=2},coverage),
            "并发修订使汇总与逐日计数不一致时拒绝输出");
        Reject(()=>ExitAlertReadiness.Analyze(acceptance,coverage with{Days=[days[0],days[0],..days.Skip(2)]}),
            "重复日期不能冒充独立交易日");
        Reject(()=>ExitAlertReadiness.Analyze(acceptance,coverage with{Ticker="510050"}),
            "跨证券响应不能拼接成验收报告");
        Reject(()=>ExitAlertReadiness.Analyze(acceptance,coverage with{Days=[..days[..^1],new CoverageDay(through.AddDays(1),"Accepted","future")]}),
            "区间外未来日期不能计入验收");
    }
}
