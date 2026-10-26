using System.Text;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeam.Jobs;

internal static class MarketActivityChecks
{
    private static readonly DateOnly Day=new(2026,10,13);
    private const string Sse="""{"sqlId":"COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C","actionErrors":[],"result":[{"PRODUCT_CODE":"01","TRADE_DATE":"20261013","TRADE_AMT":"5471.02","TRADE_VOL":"441.21"},{"PRODUCT_CODE":"17","TRADE_DATE":"20261013","TRADE_AMT":"7804.23","TRADE_VOL":"484.13"}]}""";
    private const string Szse="""[{"metadata":{"catalogid":"scsj_gprdgk_after","tabkey":"tab1","cols":{"gp":"深市合计"},"conditions":[{"name":"txtQueryDate","defaultValue":"2026-10-13"}]},"data":[{"zbmc":"成交金额（亿元）","gp":"8,976.31"},{"zbmc":"成交量（亿）","gp":"562.44"}],"error":null}]""";
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS 市场温度："+name);}
    private static void Reject(Action action,string name){try{action();}catch{Console.WriteLine("PASS 市场温度："+name);return;}throw new Exception(name);}
    public static void Run(string root)
    {
        var sse=OfficialMarketActivitySource.Parse("SSE",Day,Encoding.UTF8.GetBytes(Sse))!;
        var szse=OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse))!;
        Check(sse.AmountYuan==780423000000m && szse.VolumeShares==56244000000m,"官方分类只取总计一次，单位换算准确");
        Reject(()=>OfficialMarketActivitySource.Parse("SSE",Day.AddDays(1),Encoding.UTF8.GetBytes(Sse)),"沪市旧日期拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day.AddDays(1),Encoding.UTF8.GetBytes(Szse)),"深市旧日期拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse.Replace("亿元","万元"))),"成交金额单位改变拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SZSE",Day,Encoding.UTF8.GetBytes(Szse.Replace("8,976.31","89,76.31"))),"错误千分位拒绝");
        Reject(()=>OfficialMarketActivitySource.Parse("SSE",Day,Encoding.UTF8.GetBytes(Sse.Replace("7804.23","-1"))),"负数成交拒绝");
        var raw=new RawEvidenceStore(Path.Combine(root,"activity-raw"));
        using var repository=TestMarketData.Open(Path.Combine(root,"activity-data"),raw);
        MarketActivitySource Source(string market,string content,MarketActivityObservation observation)=>new(OfficialMarketActivitySource.Request(market,Day),Encoding.UTF8.GetBytes(content),market,Day,observation);
        var a=Source("SSE",Sse,sse);var b=Source("SZSE",Szse,szse);
        repository.SaveMarketActivity("SSE",Day,a);
        Check(repository.MarketActivity().Single() is {AmountYuan:null,VolumeShares:null},"单市场不冒充沪深总额");
        repository.SaveMarketActivity("SZSE",Day,b);repository.SaveMarketActivity("SSE",Day,a);
        Check(repository.MarketActivity().Single() is {AmountYuan:1678054000000m,VolumeShares:104657000000m},"两市同日总额与成交量，重复采集不重复计算");
        repository.SaveMarketActivity("SSE",Day,null);
        Check(repository.MarketActivity().Single().AmountYuan==1678054000000m,"后续请求失败保留已核验事实");
        repository.RecordMarketActivityCursor(Day);Check(repository.ReadMarketActivityCursor()==Day && repository.ReadSyncCursor() is null,"成交游标与ETF游标独立");
        var dates=Enumerable.Range(0,22).Select(i=>new MarketActivityPoint(Day.AddDays(i),100m+i,1000,"Complete")).ToArray();
        var metrics=MarketActivityMetrics.Calculate(dates);
        Check(metrics[19].Mean20 is null && metrics[20] is {Mean20:109.5m,Mean5:117m} && metrics[20].ActivityRatio==120m/109.5m,"前20条记录均值不含当日，样本不足不填充");
        var gap=MarketActivityMetrics.Calculate(dates.Select((x,i)=>i==19?x with{State="Incomplete",AmountYuan=null}:x).ToArray());
        Check(gap[20].Mean5 is null && gap[21].Mean20 is null,"已知缺口中断均值窗口");
        Check(MarketActivityMetrics.Calculate(Enumerable.Range(0,21).Select(i=>new MarketActivityPoint(Day.AddDays(i),0,0,"Complete")).ToArray())[^1].ActivityRatio is null,"零基准不除零");
        File.WriteAllText(Path.Combine(root,"activity-raw",Convert.ToHexString(SHA256.HashData(a.Content))+".raw"),"tampered");
        try{repository.MarketActivity();throw new Exception("未登记损坏仍返回市场总额。");}
        catch(InvalidDataException){Check(true,"市场总额查询在巡检前失败关闭");}
        repository.ReconcileEvidenceIntegrity();
        Check(repository.MarketActivity().Single().AmountYuan is null,"官方原件损坏阻断总额");
        var conflictRaw=new RawEvidenceStore(Path.Combine(root,"activity-conflict-raw"));
        using var conflict=TestMarketData.Open(Path.Combine(root,"activity-conflict-data"),conflictRaw);
        conflict.SaveMarketActivity("SSE",Day,a);conflict.SaveMarketActivity("SZSE",Day,b);
        conflict.SaveMarketActivity("SSE",Day,Source("SSE",Sse.Replace("7804.23","7805.23"),sse with{AmountYuan=sse.AmountYuan+100000000m}));
        Check(conflict.MarketActivity().Single().AmountYuan is null,"官方不同值修订保留并阻断冲突日");
    }
    /// <summary>回归“秒完成但零请求”：区分冷却跳过、已请求但无数据和实际取得两市记录。</summary>
    public static async Task RunSyncDiagnosticsAsync(string root)
    {
        Directory.CreateDirectory(root);
        async Task<MarketActivitySyncReport> Collect(string name,IMarketActivitySource source,DateOnly from,DateOnly to)
        {
            var directory=Path.Combine(root,name);
            var raw=new RawEvidenceStore(Path.Combine(directory,"raw"));
            await using var workspace=new NationalTeamApplication(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(directory,"data"),raw),raw);
            await workspace.InitializeAsync(CancellationToken.None);
            var progress=new List<EtfProgress>();workspace.SetProgressSink(p=>{progress.Add(p);return Task.CompletedTask;});
            workspace.Activity.SetMarketActivitySource(source);
            var result=await workspace.Activity.SyncMarketActivityReportAsync(from,to,CancellationToken.None);
            var rows=await workspace.Activity.MarketActivityAsync(CancellationToken.None);
            Check(rows.Count==result.QueriedDays || result.HasMore,"记录的完成天数与已登记尝试一致："+name);
            var totals=progress.Single(x=>x.Stage=="MarketRangeCompleted");var daily=progress.Where(x=>x.Stage=="MarketDayCompleted").ToArray();
            Check(daily.Length==result.QueriedDays&&daily.Sum(x=>x.Received)==result.ReceivedRows&&totals.Received==result.ReceivedRows
                &&totals.Reused==result.ExistingSources&&totals.Closed==result.ClosedSources&&totals.Empty==result.EmptySources
                &&totals.Issues==result.SourceIssues&&daily.Sum(x=>x.Deferred)==totals.Deferred,"当日及区间分类计数准确："+name);
            if(name=="cooled")Check(totals.Deferred==10,"未发起请求的冷却日期明确计为待重试");
            return result;
        }

        var downloaded=await Collect("downloaded",new FixtureActivitySource((market,day)=>
            new(OfficialMarketActivitySource.Request(market,day),Encoding.UTF8.GetBytes("fixture-"+market),market,day,
                new(market,day,100000000m,100000000m))),Day,Day);
        Check(downloaded is {QueriedDays:1,RequestedSources:2,ReceivedRows:2,SourceIssues:0,ClosedSources:0,EmptySources:0,HasMore:false},
            "两市合成成交额完成：来源调用2次、取得2条、不伪报休市或失败");

        var empty=await Collect("empty",new FixtureActivitySource((market,day)=>
            new(OfficialMarketActivitySource.Request(market,day),Encoding.UTF8.GetBytes("empty-"+market),market,day,null)),Day,Day);
        Check(empty is {QueriedDays:1,RequestedSources:2,ReceivedRows:0,EmptySources:2,SourceIssues:0},
            "空响应作为无数据，不计为成功下载成交额");

        var cooled=await Collect("cooled",new FixtureActivitySource((_,_)=>
            throw new InvalidOperationException("来源冷却中，最早重试时间2026-10-22。")),Day,Day.AddDays(5));
        Check(cooled is {QueriedDays:6,RequestedSources:2,ReceivedRows:0,SourceIssues:2,FirstFailureCode:"SourceCooldown",HasMore:true},
            "每个冷却市场仅尝试一次，不把后续日期记录为已请求");

        var unavailable=await Collect("source-invalid",new FixtureActivitySource((_,_)=>
            throw new InvalidDataException("官方成交统计响应失败。")),Day,Day);
        Check(unavailable is {QueriedDays:1,RequestedSources:2,ReceivedRows:0,SourceIssues:2,FirstFailureCode:"InvalidOfficialResponse"},
            "官方返回异常不冒充已成功下载成交额");
        await RunResumeChecks(root);
    }

    private static async Task RunResumeChecks(string root)
    {
        var directory=Path.Combine(root,"durable-resume");var raw=new RawEvidenceStore(Path.Combine(directory,"raw"));
        var calls=new List<(string Market,DateOnly Date)>();var missing=(Market:"SSE",Date:Day.AddDays(1));
        MarketActivitySource Value(string market,DateOnly day)=>new(OfficialMarketActivitySource.Request(market,day),
            Encoding.UTF8.GetBytes($"fixture-{market}-{day}"),market,day,new(market,day,100,200));
        async Task<MarketActivitySyncReport> Run(IMarketActivitySource source)
        {
            // 每轮重建会话，证明续采依据落盘事实，服务重启不丢失进度。
            await using var workspace=new NationalTeamApplication(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(directory,"data"),raw),raw);
            await workspace.InitializeAsync(default);workspace.Activity.SetMarketActivitySource(source);
            return await workspace.Activity.SyncMarketActivityReportAsync(Day,Day.AddDays(2),default);
        }
        var first=await Run(new FixtureActivitySource((market,day)=>
        {calls.Add((market,day));if((market,day)==missing)throw new System.Text.Json.JsonException("synthetic malformed response");return Value(market,day);}));
        Check(first is {ReceivedRows:5,SourceIssues:1}&&first.FirstFailureDate==missing.Date&&first.FirstFailureMarket==missing.Market,
            "局部解析失败仍保存其余5条，检查点保留失败市场日期");
        calls.Clear();var second=await Run(new FixtureActivitySource((market,day)=>{calls.Add((market,day));return Value(market,day);}));
        Check(calls.SequenceEqual([missing])&&second is{ReceivedRows:1,ExistingSources:5,SourceIssues:0,HasMore:false},
            "会话重建后只请求唯一缺口，不重采已提交的5个市场日期");
        calls.Clear();var third=await Run(new FixtureActivitySource((market,day)=>{calls.Add((market,day));throw new Exception("不应再次请求");}));
        Check(calls.Count==0&&third is{ReceivedRows:0,ExistingSources:6,SourceIssues:0},"重复历史请求全部复用已核验事实");

        var until=DateTimeOffset.UtcNow.AddHours(2);var progress=new List<EtfProgress>();
        var coldRoot=Path.Combine(root,"independent-markets");var coldRaw=new RawEvidenceStore(Path.Combine(coldRoot,"raw"));
        await using(var workspace=new NationalTeamApplication(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(coldRoot,"data"),coldRaw),coldRaw))
        {
            await workspace.InitializeAsync(default);workspace.SetProgressSink(p=>{progress.Add(p);return Task.CompletedTask;});calls.Clear();
            workspace.Activity.SetMarketActivitySource(new FixtureActivitySource((market,day)=>
            {calls.Add((market,day));if(market=="SSE")throw new CRS.Worker.Abstractions.SourceBudgetUnavailableException("SourceCooldown",until);return Value(market,day);}));
            var cold=await workspace.Activity.SyncMarketActivityReportAsync(Day,Day.AddDays(2),default);
            Check(calls.Count(x=>x.Market=="SSE")==1&&calls.Count(x=>x.Market=="SZSE")==3&&cold is{ReceivedRows:3,HasMore:true},
                "沪市冷却只请求一次，深市全部日期继续采集");
            Check(cold.RetryAt==until&&progress.Last() is{Stage:"SourceCoolingDown",Until:{} deadline}&&deadline==until,
                "实际来源冷却期限贯穿结果及最终进度，末尾不丢失Until");
        }
        using var repository=TestMarketData.Open(Path.Combine(directory,"data"),raw);
        var original=Value("SSE",Day);
        repository.SaveMarketActivity("SSE",Day,original with{Content=Encoding.UTF8.GetBytes("conflicting-revision"),Observation=original.Observation! with{AmountYuan=999}});
        Check(!repository.VerifiedMarketActivityDates(Day,Day).Contains(("SSE",Day)),"冲突修订不能被续采认定为已完成");
        var conflictResult=await Run(new FixtureActivitySource(Value));
        Check(conflictResult is{SourceIssues:1,FirstFailureCode:"ObservationUnverified"},"冲突未审核时重下载同值也不能返回采集成功");
        var hash=Convert.ToHexString(SHA256.HashData(Value("SZSE",Day).Content));File.WriteAllText(Path.Combine(directory,"raw",hash+".raw"),"tampered");
        Check(!repository.VerifiedMarketActivityDates(Day,Day).Contains(("SZSE",Day)),"原件损坏不能被续采跳过");
    }

    private sealed class FixtureActivitySource(Func<string,DateOnly,MarketActivitySource> next):IMarketActivitySource
    {
        public Task<MarketActivitySource> FetchAsync(string market,DateOnly day,CancellationToken token)
        {token.ThrowIfCancellationRequested();return Task.FromResult(next(market,day));}
    }

    public static async Task RunLive(string root)
    {
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"policy")));
        var source=new OfficialMarketActivitySource(gateway);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        foreach(var market in new[]{"SSE","SZSE"})
        {
            var result=await source.FetchAsync(market,Day,default);
            Check(result.Observation is not null,"实际官网日期和字段解析 "+market);
            repository.SaveMarketActivity(market,Day,result);
        }
        var point=repository.MarketActivity().Single();
        Check(point is {AmountYuan:1678054000000m,VolumeShares:104657000000m,ActivityRatio:null},"2026-10-13实际两市股票统计汇总与原件存证，未伪造20记录均值");
        Console.WriteLine("只读官网样本核验完成，隔离证据目录："+root);
    }
}
