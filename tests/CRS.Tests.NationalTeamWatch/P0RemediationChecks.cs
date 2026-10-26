using System.Text;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>隔离夹具验证失败、恢复和语义约束；不作为官方来源实测或连续运行验收。</summary>
internal static class P0RemediationChecks
{
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS P0："+label);}
    private static string Shares(string amount)=>"""{"actionErrors":[],"pageHelp":{"pageNo":1,"pageCount":1,"total":1},"result":[{"STAT_DATE":"2026-09-01","SEC_CODE":"510300","TOT_VOL":"AMOUNT"}]}""".Replace("AMOUNT",amount);
    public static async Task Run(string root)
    {
        var data=Path.Combine(root,"p0-data");var raw=new RawEvidenceStore(Path.Combine(root,"p0-raw"));var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,9,1);
        using(var setup=TestMarketData.Open(data,raw))
            foreach(var item in setup.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))setup.SetMembership(new(item.Market,item.Ticker),item.Name,item.IndexGroup,false,"P0 deterministic fixture");
        var http=new ResponseHttp();
        var szse=new OfficialShareSources(new ResponseHttp{Status=429,Body="range limited fixture"},raw);
        await szse.PrepareRangeAsync(day,day.AddDays(1),default);
        foreach(var date in new[]{day,day.AddDays(1)})
            Check((await szse.FetchAsync(date,[new("SZSE","159919")],default)).Failures?.Single() is {State:"AccessBlocked",Reason:"Http429",Ticker:"159919",OriginalHash:not null},"深市区间失败缓存保留限流原因及原件，不退化为普通缺失");
        await using(var workspace=new NationalTeamApplication(new QueryOnlyContext(),()=>TestMarketData.Open(data,raw),raw))
        {
            await workspace.InitializeAsync(default);workspace.Etf.SetOfficialSource(new OfficialShareSources(http,raw));
            http.Status=403;http.Body="blocked response fixture";
            await workspace.Etf.SyncAsync(day,day,default);
            var blocked=(await workspace.Etf.CollectionAttemptsAsync(key,day,day,default)).Single();
            Check(blocked is {State:"AccessBlocked",Reason:"Http403",ParserVersion:"sse-etf-json-v1"}&&blocked.SourceUrl?.Contains("sse.com.cn")==true&&blocked.OriginalHash==raw.Save(Encoding.UTF8.GetBytes(http.Body)),"403原件、来源、解析版本和访问受限状态可追溯");
            Check((await workspace.Etf.CoverageAsync(key,day,day,default)) is {State:"NoCalendar",AcceptedDays:0},"无日历仍可独立查询失败尝试，不伪造覆盖率");
            http.Status=200;http.Body="<html>invalid JSON fixture</html>";await workspace.Etf.SyncAsync(day,day,default);
            Check((await workspace.Etf.CollectionAttemptsAsync(key,day,day,default))[0] is {State:"ParseFailed",OriginalHash:not null},"错误响应保留原件并显示解析失败");
            http.Body=Shares("100");await workspace.Etf.SyncAsync(day,day,default);
            var accepted=(await workspace.Etf.CollectionAttemptsAsync(key,day,day,default))[0];
            Check(accepted.State=="Accepted"&&(await workspace.Etf.PointsAsync(key,default)).Single().Shares==1000000,"补采成功必须有通过质量检查的有效事实");
            await workspace.Etf.RegisterCalendarAsync("SSE",2026,Encoding.UTF8.GetBytes(DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source("SSE",2026),default);
            http.Body="""{"actionErrors":[],"pageHelp":{"pageNo":1,"pageCount":0,"total":0},"result":[]}""";
            await workspace.Etf.SyncAsync(day,day,default);
            var coverage=await workspace.Etf.CoverageAsync(key,day,day,default);
            Check(coverage is {AcceptedDays:1,CoveragePercent:100}&&coverage.Days.Single().LastAttempt?.State=="NotPublished","空响应不补零、不覆盖既有有效事实，单列最新采集失败");
            http.Body=Shares("200");await workspace.Etf.SyncAsync(day,day,default);
            Check((await workspace.Etf.CollectionAttemptsAsync(key,day,day,default))[0].State=="NeedsReview"&&(await workspace.Etf.PointsAsync(key,default)).Count==0,"异常跳变不能凭任务成功升级有效事实");
            var revision=(await workspace.Etf.EvidenceAsync(default)).Single(x=>x.State=="NeedsReview");await workspace.Etf.ReviewAsync(revision.RevisionId,true,"fixture-reviewer",true,default);
            await workspace.Etf.SyncAsync(day,day,default);
            Check((await workspace.Etf.CollectionAttemptsAsync(key,day,day,default))[0].State=="Accepted"&&(await workspace.Etf.PointsAsync(key,default)).Single().Shares==2000000,"审核后重试关联有效修订，历史失败记录仍保留");
            var source=new BlockingOfficialSource();workspace.Etf.SetOfficialSource(source);using var cancellation=new CancellationTokenSource();
            var sync=workspace.Etf.SyncAsync(day,day,cancellation.Token);await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancellation.Cancel();
            try{await sync;throw new Exception("取消未生效");}catch(OperationCanceledException){ }
            Check((await workspace.Etf.CollectionAttemptsAsync(key,day,day,default))[0].State=="Interrupted","宿主取消后持久记录中断状态");
        }
        using(var reopened=TestMarketData.Open(data,raw))
        {
            var attempts=reopened.CollectionAttempts(key,day,day);
            Check(attempts.Count==7&&attempts[0].State=="Interrupted"&&attempts.Any(x=>x.State=="AccessBlocked")&&attempts.Count(x=>x.State=="Accepted")==2,"重启保留失败、修复及取消历史");
            var overview=reopened.Overview().Single();
            Check(overview.EvidenceLevel==EvidenceLevel.MarketObservation&&overview.Basis.Contains("不代表任何机构"),"启用观察或份额翻倍均不能升级A类机构持仓");
            var payload=System.Text.Json.JsonSerializer.Serialize(overview).Replace("\"EvidenceLevel\":1","\"EvidenceLevel\":2");
            Check(System.Text.Json.JsonSerializer.Deserialize<EtfSummary>(payload)!.EvidenceLevel==EvidenceLevel.MarketObservation,"外部传入证据等级不能把市场数据升级为A类事实");
        }
        var equityRaw=new RawEvidenceStore(Path.Combine(root,"p0-equity-raw"));using var equity=TestMarketData.Open(Path.Combine(root,"p0-equity-data"),equityRaw);DirectEquityChecks.Seed(equity);
        var holding=equity.EquityHoldings(null,"汇金资管",null,false).Single();
        Check(holding is {Delta:-100,EvidenceLevel:EvidenceLevel.DisclosedHolding,Relationship:HoldingRelationship.DirectEquityHolding,EvidencePage:"P11 股东表"}&&holding.EvidenceHash?.Length==64&&!holding.StatusText.Contains("确认净"),"A类股票快照具备逐账户原文位置和哈希，差额不称确认交易");
        Check((holding with{EvidencePage=null}).EvidenceLevel==EvidenceLevel.ResearchHypothesis,"缺少可定位原文不得标记A类持仓");
        Check(holding.Basis.Contains("不与ETF间接暴露合并")&&EtfHistory.Calculate([new(key,[day],[new(day,1000000,null,"fixture")])]).Single() is {Shares:1000000,EvidenceLevel:EvidenceLevel.MarketObservation},"直接股票记录不进入ETF市场份额汇总，禁止将市场篮子当机构穿透暴露");
        await QueryPaging(root,key,day);
        await ValidationBackoff(root,day);
    }
    private static async Task ValidationBackoff(string root,DateOnly day)
    {
        var path=Path.Combine(root,"contract-backoff");var raw=new RawEvidenceStore(Path.Combine(path,"raw"));var handler=new HtmlHandler();
        using(var policy=new CRS.Plugins.NationalTeam.Jobs.SourceRequestPolicy(Path.Combine(path,"policy")))
        using(var gateway=new CRS.Plugins.NationalTeam.Jobs.OfficialHttpGateway(policy,handler))
        {
            var source=new OfficialShareSources(gateway,raw);
            var first=await source.FetchAsync(day,[new("SSE","510300")],default);
            var second=await source.FetchAsync(day.AddDays(1),[new("SSE","510300")],default);
            Check(first.Failures?.Single().State=="ParseFailed"&&second.Failures?.Single().State=="SourceFailed"&&handler.Requests==1,"HTTP200防护HTML触发契约失败持久退避，不连续请求同一来源");
        }
        using var reopened=new CRS.Plugins.NationalTeam.Jobs.SourceRequestPolicy(Path.Combine(path,"policy"));
        Check(reopened.Read("query.sse.com.cn").Blocked>DateTimeOffset.UtcNow,"结构/解析错误冷却在策略库重建后保留");
    }
    private sealed class HtmlHandler:System.Net.Http.HttpMessageHandler
    {
        public int Requests;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)
        {Requests++;return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent("<html>synthetic access challenge; no facts</html>")});}
    }
    private static async Task QueryPaging(string root,EtfKey key,DateOnly day)
    {
        var directory=Path.Combine(root,"p0-query");var raw=new RawEvidenceStore(Path.Combine(directory,"raw"));
        using(var seed=TestMarketData.Open(Path.Combine(directory,"data"),raw))
            for(var i=0;i<15;i++)seed.RecordCollection(seed.BeginCollection(key,day),"Interrupted","FixtureRestart");
        await using var session=new CRS.Plugins.NationalTeam.Jobs.NationalTeamBackgroundSession(new SessionContext(directory),new ResponseHttp());
        await session.StartAsync(default);
        var request=new EtfRequest("collection-attempts",key.Market,key.Ticker,day,day);var ids=new HashSet<string>();var pages=0;
        while(true)
        {
            var response=await session.QueryAsync(System.Text.Json.JsonSerializer.Serialize(request),default);
            var page=System.Text.Json.JsonSerializer.Deserialize<EtfQueryPage>(response)!;
            var attempts=System.Text.Json.JsonSerializer.Deserialize<List<CollectionAttempt>>(page.Items)!;
            foreach(var attempt in attempts)Check(ids.Add(attempt.Id),"采集尝试分页不重复");pages++;
            if(page.NextOffset is not {} next)break;request=request with{Offset=next,Snapshot=page.Snapshot};
        }
        Check(pages==2&&ids.Count==15,"生产后台查询跨页完整返回历史尝试，元数据不携带无界数组");
    }
    private sealed class SessionContext(string directory):CRS.Worker.Abstractions.IBackgroundHostContext
    {
        public string PluginId=>"crs.national-team";
        public string DataDirectory=>directory;
        public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();
    }
    private sealed class ResponseHttp:IPluginHttpGateway
    {
        public int Status=200;public string Body="";
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>Task.FromResult(new PublicHttpResponse(Status,Encoding.UTF8.GetBytes(Body),"application/json"));
    }
}
