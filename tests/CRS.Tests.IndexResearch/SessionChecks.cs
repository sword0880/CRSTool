using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;

/// <summary>真实后台会话、原件文件HTTP替身和显式合成日历；不访问正式服务或源站。</summary>
internal static class SessionChecks
{
    public static async Task Run(string sources,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.IndexSession",Guid.NewGuid().ToString("N"));var context=new Context(root);Seed(context);
        var approvals=new SyntheticApprovals();var fixture=new Fixture(sources);await using var session=new NationalTeamBackgroundSession(context,fixture,indexSourceApprovals:approvals);await session.StartAsync(default);
        check(fixture.Calls==0&&session.DescribeJobs().Where(x=>x.JobId.StartsWith("index-",StringComparison.Ordinal)).All(x=>!x.EnabledByDefault&&!x.RunOnStartup&&x.ManualOnly),"新指数任务默认关闭且启动只读本地存储");
        var wrongHqApprovals=new SyntheticApprovals{WrongHqBase=true};var wrongHqHttp=new Fixture(sources);
        await using(var wrongHq=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"wrong-hq")),wrongHqHttp,indexSourceApprovals:wrongHqApprovals))
        {
            await wrongHq.StartAsync(default);
            check((await wrongHq.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid())).Status=="Rejected"&&wrongHqHttp.Calls==0,
                "行情数据子域名必须有独立准确的来源基址审批，错误主机零HTTP");
        }
        var from=new DateOnly(2026,10,20);var through=new DateOnly(2026,10,23);var rootId=Guid.NewGuid();
        string Request(string operation,bool refresh=false,string? ticker=null,DateOnly? day=null)=>JsonSerializer.Serialize(new EtfRequest(operation,Ticker:ticker,From:operation=="index-series-sync"?from:null,To:day??through,RefreshSource:refresh));
        var invalid=await session.ExecuteAsync("index-series-sync",Request("index-series-sync",ticker:"600036"),default,Guid.NewGuid());check(invalid.Status=="Rejected"&&fixture.Calls==0,"错误指数代码在联网前拒绝");
        var blockedHttp=new Fixture(sources);await using(var blocked=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"unapproved")),blockedHttp))
        {
            await blocked.StartAsync(default);
            check((await blocked.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid())).Checkpoint?.Contains("SourceApprovalRequired",StringComparison.Ordinal)==true&&blockedHttp.Calls==0,"生产默认缺少来源审批，价格回填在HTTP前拒绝");
            check((await blocked.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,Guid.NewGuid())).Checkpoint?.Contains("SourceApprovalRequired",StringComparison.Ordinal)==true&&blockedHttp.Calls==0,"生产默认缺少来源审批，估值采集在HTTP前拒绝");
        }
        approvals.Uses=MarketCycleSourceUse.AutomatedCollection|MarketCycleSourceUse.PersistentStorage;
        check((await session.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid())).Status=="Rejected"&&fixture.Calls==0,"自动采集许可不能代替历史回填用途");
        approvals.Uses|=MarketCycleSourceUse.HistoricalBackfill;approvals.EvidenceHealthy=false;
        check((await session.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid())).Status=="Rejected"&&fixture.Calls==0,"审批条款原件不健康时不能联网");approvals.EvidenceHealthy=true;
        var result=await session.ExecuteAsync("index-series-sync",Request("index-series-sync",true),default,rootId);
        check(result.Status=="Succeeded"&&fixture.Calls==14,"11指数和共享两份方法/单位页面只产生14次实际HTTP");
        check(result.Checkpoint?.Contains("added=44",StringComparison.Ordinal)==true,"后台记录实际新增44个日线事实");
        var calls=fixture.Calls;var replay=await session.ExecuteAsync("index-series-sync",Request("index-series-sync",true),default,rootId);
        check(replay.Status=="Succeeded"&&fixture.Calls==calls&&replay.Checkpoint?.Contains("added=0",StringComparison.Ordinal)==true,"同请求沿子命令回执恢复且不制造新增/HTTP");
        var reuse=await session.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid());
        check(reuse.Status=="Succeeded"&&fixture.Calls==calls&&reuse.Checkpoint?.Contains("reused=44",StringComparison.Ordinal)==true,"新补缺请求验证已登记事实后零HTTP复用");
        var valuationId=Guid.NewGuid();var valuation=await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,valuationId);
        check(valuation.Status=="Succeeded"&&fixture.Calls==calls+4,"前后日期头/方法/列表四份证明绑定10个估值快照");
        calls=fixture.Calls;var valuationAgain=await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,valuationId);
        check(valuationAgain.Status=="Succeeded"&&fixture.Calls==calls&&valuationAgain.Checkpoint?.Contains("freshAdded=0",StringComparison.Ordinal)==true,"估值回执重放核对当前证明但不重新下载");
        fixture.BadAfter=true;fixture.Headers=0;
        check((await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,Guid.NewGuid())).Status=="NeedsReview"&&fixture.Rejections==1,"后置日期头畸形归为来源合同待审，而非宿主故障");fixture.BadAfter=false;fixture.Rejections=0;calls=fixture.Calls;
        var wrongDate=await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync",day:through.AddDays(-1)),default,valuationId);
        check(wrongDate.Status=="Rejected"&&fixture.Calls==calls,"不能用另一日期的估值回执绕过当前请求");
        var newWrongDate=await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync",day:through.AddDays(-1)),default,Guid.NewGuid());
        check(newWrongDate.Status=="Rejected"&&fixture.Rejections==0,"所选日期与官网统计日不同是请求边界，不归责源站");
        var child=new Guid(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{Version="index-range-child-v1",root=rootId,code="399317",from,through})).AsSpan(0,16));
        calls=fixture.Calls;var wrongKind=await session.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,child);
        check(wrongKind.Status=="Rejected"&&fixture.Calls==calls,"价格子回执不能冒充估值根回执");
        var query=await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("index-series-report",From:from,To:through)),default);
        check(query.Contains("CurrentKnowledgeResearch",StringComparison.Ordinal)&&query.Contains("NotProven",StringComparison.Ordinal),"后台研究报告固定携带回看和未验证边界");

        var missing=new Context(Path.Combine(root,"no-calendar"));var noCalendarHttp=new Fixture(sources);await using(var noCalendar=new NationalTeamBackgroundSession(missing,noCalendarHttp,indexSourceApprovals:approvals))
        {await noCalendar.StartAsync(default);check((await noCalendar.ExecuteAsync("index-series-sync",Request("index-series-sync"),default,Guid.NewGuid())).Status=="Rejected"&&noCalendarHttp.Calls==0,"缺核验日历不猜工作日或启动下载");}
        var noPricesContext=new Context(Path.Combine(root,"no-prices"));Seed(noPricesContext);var noPricesHttp=new Fixture(sources);await using(var noPrices=new NationalTeamBackgroundSession(noPricesContext,noPricesHttp,indexSourceApprovals:approvals))
        {await noPrices.StartAsync(default);check((await noPrices.ExecuteAsync("index-valuation-sync",Request("index-valuation-sync"),default,Guid.NewGuid())).Status=="Rejected"&&noPricesHttp.Calls==1,"仅有日期头而缺价格不继续下载或批准PB");}
        var retryContext=new Context(Path.Combine(root,"retry"));Seed(retryContext);var retryHttp=new Fixture(sources){FailOn=1};await using(var retry=new NationalTeamBackgroundSession(retryContext,retryHttp,indexSourceApprovals:approvals))
        {
            await retry.StartAsync(default);var retryId=Guid.NewGuid();check((await retry.ExecuteAsync("index-series-sync",Request("index-series-sync",ticker:"399317"),default,retryId)).Status=="RetryDeferred","HTTP失败保留可退避状态而不声称完成");
            retryHttp.FailOn=0;check((await retry.ExecuteAsync("index-series-sync",Request("index-series-sync",ticker:"399317"),default,retryId)).Status=="Succeeded","同一失败请求恢复后仍沿稳定身份提交");
        }
    }
    private static void Seed(Context context)
    {
        var raw=new RawEvidenceStore(Path.Combine(context.DataDirectory,"raw"));using var repo=TestMarketData.Open(Path.Combine(context.DataDirectory,"data"),raw);
        const string calendar="合成夹具：2026年部分节假日休市安排，周末休市。1月1日（星期四）至1月3日（星期六）休市；2月15日（星期日）至2月23日（星期一）休市；4月4日（星期六）至4月6日（星期一）休市；5月1日（星期五）至5月5日（星期二）休市；6月19日（星期五）至6月21日（星期日）休市；9月25日（星期五）至9月27日（星期日）休市；10月1日（星期四）至10月7日（星期三）休市";
        repo.RegisterReviewedCalendar("SZSE",2026,Encoding.UTF8.GetBytes(calendar),ReviewedExchangeCalendar.Source("SZSE",2026));
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    // 明确合成的许可夹具，仅存在测试进程；不写入正式审批台账，也不代表真实来源授权。
    private sealed class SyntheticApprovals:IIndexSeriesSourceApprovals
    {
        private readonly byte[] terms="SYNTHETIC TEST ONLY - NOT A SOURCE LICENSE"u8.ToArray();
        private readonly DateTimeOffset reviewedAt=DateTimeOffset.UtcNow.AddMinutes(-1);
        public MarketCycleSourceUse Uses=MarketCycleSourceUse.AutomatedCollection|MarketCycleSourceUse.PersistentStorage|MarketCycleSourceUse.HistoricalBackfill;
        public bool EvidenceHealthy=true;
        public bool WrongHqBase;
        public MarketCycleSourceApproval? Find(string dataset)
        {
            var isHq=dataset=="cnindex-published-prices-hq";
            var sourceId=isHq?"CNINDEX-HQ":"CNINDEX";
            var sourceUri=isHq&&!WrongHqBase?new Uri("https://hq.cnindex.com.cn/"):new Uri("https://www.cnindex.com.cn/");
            return new(sourceId,sourceUri,dataset,"cnindex-index-series-v1",OfficialIndexSeriesSource.ParserVersion,MarketCycleSourceApprovalStatus.Approved,Uses,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(terms)),"SyntheticFixture",reviewedAt,"synthetic-v1");
        }
        public bool Verify(string hash)=>EvidenceHealthy&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(terms))==hash;
    }
    private sealed class Fixture(string root):IPluginHttpGateway,IOfficialSourceValidationFeedback
    {
        public int Calls,FailOn,Rejections,Headers;public bool BadAfter;
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls++;if(Calls==FailOn)return Task.FromResult(new PublicHttpResponse(503,[],"text/plain"));
            if(!OfficialIndexSeriesSource.IsSource(uri))throw new InvalidDataException("测试不允许未知来源。");
            string name;
            if(uri==OfficialIndexSeriesSource.UnitsSource)name="index-detail.html";
            else if(uri==OfficialIndexSeriesSource.DaySource){Headers++;if(BadAfter&&Headers==2)return Task.FromResult(new PublicHttpResponse(200,"{bad"u8.ToArray(),"application/json"));name="query-day.json";}
            else if(uri==OfficialIndexSeriesSource.ValuationSource)name="industry-index-list-1.json";
            else if(uri.AbsolutePath.EndsWith("gz_399317.pdf",StringComparison.Ordinal))name="method-a.pdf";
            else if(uri.AbsolutePath.EndsWith("gz_399381.pdf",StringComparison.Ordinal))name="method-sector.pdf";
            else{var code=System.Web.HttpUtility.ParseQueryString(uri.Query)["indexCode"]!;name=code=="399317"?"cni-a-20261020-23.json":code+"-4days.json";}
            return Task.FromResult(new PublicHttpResponse(200,File.ReadAllBytes(Path.Combine(root,name)),"application/octet-stream"));
        }
        public void RejectContract(Uri uri)=>Rejections++;
    }
}
