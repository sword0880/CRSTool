using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EtfLoadingChecks
{
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS ETF加载："+label);}
    public static async Task Run()
    {
        await EtfChartChecks.Run();
        CheckHistoryParity();CheckDashboardIntegrity();CheckBeijingRollover();
        var workspace=new Workspace();using var lifetime=new CancellationTokenSource();
        var model=new EtfViewModel(workspace,lifetime.Token);
        await model.RefreshCommand.ExecuteAsync(null);
        Check(model.Rows.Count==2&&model.Series.Length==1&&model.MarketAmountSeries.Length==1,"整页一次读取同时显示表格和两张图");
        Check(workspace.SeparateReads==0,"整页刷新不重复读取概览、历史、成交额");
        model.Selected=model.Rows[0];var first=model.HistoryLoadTask;var firstToken=workspace.HistoryTokens[^1];
        model.Selected=model.Rows[1];var second=model.HistoryLoadTask;
        Check(firstToken.IsCancellationRequested,"切换ETF取消旧图表及证据请求");
        workspace.HistoryReplies[^1].SetResult([new(new(2026,10,13),200,10,1,1)]);await second;await first;
        Check(model.ChartStatus.StartsWith("510050"),"旧请求取消后只显示当前证券");
        workspace.DashboardReply=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh=model.RefreshCommand.ExecuteAsync(null);
        model.Selected=model.Rows[0];var current=model.HistoryLoadTask;
        workspace.HistoryReplies[^1].SetResult([new(new(2026,10,14),300,20,1,1)]);await current;
        workspace.DashboardReply.SetResult(workspace.Result);await refresh;
        Check(model.ChartStatus.StartsWith("510300")&&model.ChartStatus.Contains("2026-10-14"),"迟到的整页结果不覆盖用户新选择");
    }
    private static void CheckHistoryParity()
    {
        var dates=Enumerable.Range(0,434).Select(i=>new DateOnly(2025,1,1).AddDays(i)).ToArray();
        var inputs=Enumerable.Range(0,24).Select(i=>new EtfHistoryInput(new(i%2==0?"SSE":"SZSE",i.ToString("D6")),
            dates.Where((_,j)=>j%7!=i%3).Reverse().ToArray(),dates.Where((_,j)=>j%(13+i)!=0).Select((d,j)=>new SharePoint(d,1000+j,null,"r"+j)).ToArray())).ToArray();
        var timer=Stopwatch.StartNew();var expected=Reference(inputs);var before=timer.ElapsedMilliseconds;
        timer.Restart();var actual=EtfHistory.Calculate(inputs);var after=timer.ElapsedMilliseconds;
        Check(JsonSerializer.Serialize(expected)==JsonSerializer.Serialize(actual),"434日24只ETF的缺口、跨市场前一交易日和净变化与原算法一致");
        Console.WriteLine($"PROFILE synthetic history: beforeMs={before};afterMs={after};points={actual.Count}");
    }
    private static List<EtfHistoryPoint> Reference(IReadOnlyList<EtfHistoryInput> inputs)
    {
        var maps=inputs.Select(x=>x.Points.ToDictionary(p=>p.Date)).ToArray();
        return inputs.SelectMany(x=>x.Calendar.Concat(x.Points.Select(p=>p.Date))).Distinct().Order().Select(day=>
        {
            var covered=maps.Count(x=>x.ContainsKey(day));
            decimal? shares=covered==inputs.Count?maps.Sum(x=>x[day].Shares):null;
            var previous=inputs.Select(x=>x.Calendar.Where(d=>d<day).Order().LastOrDefault()).Distinct().ToArray();
            var deltas=inputs.Select(x=>EtfMetrics.Calculate(day,1,x.Calendar,x.Points).DeltaShares).ToArray();
            decimal? delta=previous.Length==1&&deltas.All(x=>x.HasValue)?deltas.Sum(x=>x!.Value):null;
            return new EtfHistoryPoint(day,shares,delta,covered,inputs.Count);
        }).ToList();
    }
    private static void CheckDashboardIntegrity()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.EtfDashboard",Guid.NewGuid().ToString("N"));
        var rawRoot=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawRoot);
        using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw);
        var bytes=Encoding.UTF8.GetBytes("synthetic official share and calendar evidence");var hash=raw.Save(bytes);
        var fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-test");var date=new DateOnly(2026,10,13);
        repo.ImportCalendar(fetch,[("SSE",date),("SZSE",date)],default);
        repo.ImportShares(fetch,[new(new("SSE","510300"),date,100m,"万份")],default);
        foreach(var key in new EtfKey?[]{null,new("SSE","510300")})
        {
            var expected=new EtfDashboard(repo.Overview(),repo.History(key),repo.MarketActivity());
            var actual=repo.ExecuteOperation(()=>repo.Dashboard(key),default);
            Check(JsonSerializer.Serialize(expected)==JsonSerializer.Serialize(actual),"合并读取与独立查询完全相同："+(key?.Ticker??"全部"));
        }
        File.WriteAllText(Path.Combine(rawRoot,hash+".raw"),"damaged");
        try{repo.ExecuteOperation(()=>repo.Dashboard(null),default);throw new Exception("损坏证据被缓存放行");}
        catch(InvalidDataException){Check(true,"下一次整页刷新仍发现新损坏原件并拒绝返回金融结果");}
        File.WriteAllBytes(Path.Combine(rawRoot,hash+".raw"),bytes);
        Check(repo.ExecuteOperation(()=>repo.Dashboard(null),default).History.Count==1,"损坏修复后重新校验，不跨刷新缓存");
    }
    private static void CheckBeijingRollover()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.EtfRollover",Guid.NewGuid().ToString("N"));var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var clock=new RolloverClock{At=new(2026,10,25,15,59,59,TimeSpan.Zero)};
        using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);
        var hash=raw.Save(Encoding.UTF8.GetBytes("synthetic calendar known before midnight; no new share fact"));
        var fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-rollover");var beforeDay=new DateOnly(2026,10,23);var newDay=new DateOnly(2026,10,26);var key=new EtfKey("SSE","510300");
        repo.ImportCalendar(fetch,[("SSE",beforeDay),("SZSE",beforeDay),("SSE",newDay),("SZSE",newDay)],default);
        repo.ImportShares(fetch,[new(key,beforeDay,100m,"万份")],default);
        var singleBefore=repo.History(key);var allBefore=repo.History(null);var evidenceBefore=JsonSerializer.Serialize(repo.Evidence(key));
        Check(singleBefore.Count==1&&allBefore.Count==1,"北京零点前不把已登记的未来交易日当成当日历史");
        clock.At=clock.At.AddSeconds(1);var singleAfter=repo.History(key);var allAfter=repo.History(null);
        Check(singleAfter.Count==2&&allAfter.Count==2&&singleAfter[^1] is{Date:var date,Shares:null,DeltaShares:null,Covered:0,Total:1}&&date==newDay
            &&allAfter[^1] is{Shares:null,DeltaShares:null,Covered:0,Total:24},"北京跨日仅新增缺口行，单只和固定篮子均不把未知份额补零");
        Check(JsonSerializer.Serialize(singleBefore)==JsonSerializer.Serialize(singleAfter.Take(1))&&JsonSerializer.Serialize(allBefore)==JsonSerializer.Serialize(allAfter.Take(1))
            &&JsonSerializer.Serialize(repo.Evidence(key))==evidenceBefore,"跨日不改已有历史值或生成新证据/金融事实");
    }
    private sealed class RolloverClock:TimeProvider
    {public DateTimeOffset At;public override DateTimeOffset GetUtcNow()=>At;}
    private sealed class Workspace:IEtfWorkspace,IEtfDashboardWorkspace
    {
        public int SeparateReads;
        public string NetworkStatus=>"synthetic";
        public List<CancellationToken> HistoryTokens=[];
        public List<TaskCompletionSource<List<EtfHistoryPoint>>> HistoryReplies=[];
        public TaskCompletionSource<EtfDashboard?>? DashboardReply;
        private static EtfSummary Row(string code)=>new("SSE",code,code,"test","DataPilot",null,null,null,null,null,"",0);
        public EtfDashboard Result=>new([Row("510300"),Row("510050")],[new(new(2026,10,13),100,null,1,2)],[new(new(2026,10,13),100000000m,1,"Complete")]);
        public Task<EtfDashboard?> DashboardAsync(EtfKey? key,CancellationToken token)=>DashboardReply?.Task??Task.FromResult<EtfDashboard?>(Result);
        public Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>Task.FromResult(new List<EtfConfiguration>());
        public Task<List<EtfSummary>> OverviewAsync(CancellationToken token){SeparateReads++;throw new Exception("重复查询");}
        public Task<List<MarketActivityPoint>> MarketActivityAsync(CancellationToken token){SeparateReads++;throw new Exception("重复查询");}
        public Task<List<EtfHistoryPoint>> HistoryAsync(EtfKey? key,CancellationToken token)
        {HistoryTokens.Add(token);var reply=new TaskCompletionSource<List<EtfHistoryPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);HistoryReplies.Add(reply);return reply.Task.WaitAsync(token);}
        public Task<List<EvidenceRow>> EvidenceAsync(CancellationToken token)=>Task.FromResult(new List<EvidenceRow>());
        public Task SaveConfigurationAsync(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,CancellationToken token)=>throw new NotSupportedException();
        public Task<List<SharePoint>> PointsAsync(EtfKey key,CancellationToken token)=>throw new NotSupportedException();
        public Task<CoverageReport> CoverageAsync(EtfKey key,DateOnly from,DateOnly to,CancellationToken token)=>throw new NotSupportedException();
        public Task<SyncReport> SyncAsync(DateOnly from,DateOnly to,CancellationToken token)=>throw new NotSupportedException();
        public Task ExportEvidenceAsync(string id,bool original,string destination,CancellationToken token)=>throw new NotSupportedException();
        public Task ReviewAsync(string id,bool accept,string reviewer,bool comparable,CancellationToken token)=>throw new NotSupportedException();
        public Task MembershipAsync(EtfKey key,string name,string index,bool active,string reason,CancellationToken token)=>throw new NotSupportedException();
    }
}
