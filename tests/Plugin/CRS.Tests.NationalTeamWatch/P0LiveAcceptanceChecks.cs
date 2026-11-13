using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
internal static class P0LiveAcceptanceChecks
{
    public static async Task EquityCase()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));var reader=new WorkerEtfWorkspace();
        var disclosures=await reader.EquityDisclosuresAsync(new("SSE","600153"),deadline.Token);
        var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","p0-live");Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"installed-600153-disclosures.json"),JsonSerializer.Serialize(disclosures,new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
        Console.WriteLine("READ 保存真实候选及逐账户数值，未提交审核。");
    }
    public static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=deadline.Token;
        var reader=new WorkerEtfWorkspace();
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var observations=new List<object>();
        foreach(var item in await reader.OverviewAsync(token))
        {
            var key=new EtfKey(item.Market,item.Ticker);var coverage=await reader.CoverageAsync(key,today.AddDays(-400),today.AddDays(-1),token);
            var points=await reader.PointsAsync(key,token);
            observations.Add(new{key.Market,key.Ticker,ValidObservations=points.Count,First=points.FirstOrDefault()?.Date,Last=points.LastOrDefault()?.Date,
                coverage.RegisteredTradingDays,coverage.AcceptedDays,coverage.MissingDays,coverage.PendingDays,coverage.CriticalDays,coverage.CalendarVerified,coverage.CalendarRangeCovered,coverage.State,
                History250Passed=coverage.AcceptedDays>=250&&coverage.CalendarVerified&&coverage.CalendarRangeCovered,
                Gaps=coverage.Days.Where(x=>x.State!="Accepted").Select(x=>new{x.Date,x.State}).ToArray()});
            Console.WriteLine($"READ {key.Market}:{key.Ticker}: actual={points.Count};calendar={coverage.State}");
        }
        var rows=await reader.EquityHoldingsAsync(null,null,null,false,token);
        var result=new{CheckedAt=DateTimeOffset.UtcNow,Use="PersonalLocalNonCommercialNoRedistribution",ReleaseApproved=false,Etfs=observations,
            EquityStates=rows.GroupBy(x=>x.State).Select(x=>new{State=x.Key,Count=x.Count()}),
            Continuous20Status="RequiresActualDailyCollectionEvidence;historicalBackfillDoesNotQualify"};
        var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","p0-live");Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"installed-acceptance.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),token);
        Console.WriteLine("READ 完成实际服务只读验收报告；未提交任何采集或审核命令。");
    }
}
