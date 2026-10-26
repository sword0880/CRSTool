using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Worker.Abstractions;
using CRS.Worker.Service;
using CRS.Worker.PluginHost;
using CRS.Worker.Ipc;
using Microsoft.Data.Sqlite;

internal static class SourceBudgetChecks
{
    private static readonly SourceBudgetState Empty = new(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0);
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.SourceBudgets",Guid.NewGuid().ToString("N"));
        var key=RandomNumberGenerator.GetBytes(32);
        var passed=0;
        void Check(bool condition,string name)
        {if(!condition)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS 共享来源预算："+name);}
        async Task Denied(Task<IBackgroundSourceLease> task,string code)
        {
            try{using var unexpected=await task;throw new Exception("请求意外获准");}
            catch(SourceBudgetUnavailableException error){Check(error.Code==code,code);}
        }
        await SourceBudgetPortChecks.RunAsync(Check);
        var clock=new Clock();
        using(var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key),clock))
        {
            var a=new WorkerPluginContext("plugin-a",root,key,sources:registry);
            var b=new WorkerPluginContext("plugin-b",root,key,sources:registry);
            // 行业来源独立串行且冷却持久，不能借基金或交易所预算绕过拒绝。
            var industry=await a.AcquireSourceAsync("www.cnindex.com.cn",Empty,default);
            var industryQueued=b.AcquireSourceAsync("hq.cnindex.com.cn",Empty,default);
            Check(!industryQueued.IsCompleted,"国证WWW/HQ跨插件共用唯一在途额度");
            using(var fund=await b.AcquireSourceAsync("www.efunds.com.cn",Empty,default))Check(registry.Snapshot().Count(x=>x.Active==1)==2,"基金与国证来源相互独立");
            industry.Complete(403);industry.Dispose();await Denied(industryQueued,"SourceCooldown");
            await Denied(a.AcquireSourceAsync("hq.cnindex.com.cn",Empty,default),"SourceCooldown");
            using(var bank=await a.AcquireSourceAsync("v.icbc.com.cn",Empty,default))
                Check(registry.Snapshot().Single(x=>x.Source=="ISSUER-601398").Active==1&&registry.Snapshot().Single(x=>x.Source=="CNINDEX").State=="Cooldown","工行发行人独立预算不清除其他来源冷却");
            Check(registry.Snapshot().Single(x=>x.Source=="CNINDEX").BlockedUtc>clock.GetUtcNow().AddHours(5.9),"国证拒绝保留实际宿主Until");
            using(var abc=await a.AcquireSourceAsync("www.abchina.com",Empty,default))
            {Check(registry.Snapshot().Single(x=>x.Source=="ISSUER-601288").Active==1&&registry.Snapshot().Single(x=>x.Source=="CNINDEX").State=="Cooldown","农行原件独立来源不清除既有国证冷却");abc.Complete(200);}
            // 公牛网站和其披露CDN是同一出版者，不能以独立来源为由绕过本发行人的拒绝。
            var gongniu=await a.AcquireSourceAsync("www.gongniu.cn",Empty,default);
            var gongniuCdn=b.AcquireSourceAsync("gnstaticcdn.gongniukaiguan.net",Empty,default);
            Check(!gongniuCdn.IsCompleted,"公牛官网/CDN跨插件共享唯一额度");
            gongniu.Complete(403);gongniu.Dispose();await Denied(gongniuCdn,"SourceCooldown");
            await Denied(a.AcquireSourceAsync("www.gongniu.cn",Empty,default),"SourceCooldown");
            Check(registry.Snapshot().Single(x=>x.Source=="ISSUER-603195").State=="Cooldown"
                &&registry.Snapshot().Single(x=>x.Source=="SSE").State!="Cooldown","公牛冷却不污染交易所来源");
            var first=await a.AcquireSourceAsync("query.sse.com.cn",Empty,default);
            var second=b.AcquireSourceAsync("static.sse.com.cn",Empty,default);
            var third=a.AcquireSourceAsync("etf.sse.com.cn",Empty,default);
            Check(!second.IsCompleted&&!third.IsCompleted,"不同插件和上交所域名共用单个并发额度");
            using(var independent=await b.AcquireSourceAsync("www.szse.cn",Empty,default))
                Check(registry.Snapshot().Count(x=>x.Active==1)==2,"不同交易所独立推进");
            Check(registry.Snapshot().Single(x=>x.Source=="SSE").Waiting==2,"共享队列长度可观测");
            clock.Advance(TimeSpan.FromSeconds(3));first.Complete(200);first.Dispose();
            using(var lease=await second.WaitAsync(TimeSpan.FromSeconds(2)))
            {Check(!third.IsCompleted,"FIFO不允许后来的插件请求插队");clock.Advance(TimeSpan.FromSeconds(3));lease.Complete(200);}
            using(var lease=await third.WaitAsync(TimeSpan.FromSeconds(2)))lease.Complete(200);
            Check(registry.Snapshot().Single(x=>x.Source=="SSE").Active==0,"释放后没有遗留占用");
            clock.Advance(TimeSpan.FromSeconds(3));
            using(var active=await a.AcquireSourceAsync("www.sse.com.cn",Empty,default))
            {
                using var cancel=new CancellationTokenSource();
                var cancelled=b.AcquireSourceAsync("query.sse.com.cn",Empty,cancel.Token);
                var following=b.AcquireSourceAsync("etf.sse.com.cn",Empty,default);
                cancel.Cancel();try{using var ignored=await cancelled;}catch(OperationCanceledException){Check(true,"排队取消保留取消语义");}
                active.Complete(429,clock.GetUtcNow().AddDays(2));
                active.Dispose();
                await Denied(following,"SourceCooldown");
            }
            await Denied(b.AcquireSourceAsync("static.sse.com.cn",Empty,default),"SourceCooldown");
            using(var late=await a.AcquireSourceAsync("www.chinacnd.com",Empty,default))
            {
                registry.ReportFailure("www.chinacnd.com",0);
                late.Complete(200);
                Check(registry.Snapshot().Single(x=>x.Source=="ISSUER-600153").State=="Cooldown","迟到成功不清除内容校验失败的冷却");
            }
            Check(registry.Snapshot().Single(x=>x.Source=="SSE").BlockedUtc>=clock.GetUtcNow().AddDays(2),"Retry-After延长共享冷却且不被排队重试清除");
            try{using var ignored=await a.AcquireSourceAsync("unreviewed.example",Empty,default);throw new Exception();}
            catch(ArgumentException){Check(true,"域名白名单由宿主决定");}
        }
        using(var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key),clock))
        {
            await Denied(registry.AcquireAsync("new-plugin","hq.cnindex.com.cn",Empty,default),"SourceCooldown");
            await Denied(registry.AcquireAsync("new-plugin","www.sse.com.cn",Empty,default),"SourceCooldown");
            clock.Advance(TimeSpan.FromDays(3));
            using(var recovered=await registry.AcquireAsync("new-plugin","www.sse.com.cn",Empty,default))recovered.Complete(200);
            Check(registry.Snapshot().Single(x=>x.Source=="SSE").Failures==0,"重启保留冷却，到期成功后恢复");
            registry.ReportFailure("www.szse.cn",0);
            var before=registry.Snapshot().Single(x=>x.Source=="SZSE");
            registry.ReportFailure("disc.static.szse.cn",503);
            var after=registry.Snapshot().Single(x=>x.Source=="SZSE");
            Check(before.BlockedUtc>=clock.GetUtcNow().AddMinutes(5)&&after.BlockedUtc>=clock.GetUtcNow().AddMinutes(10),"超时和503按来源累计指数退避");
            registry.ReportFailure("www.szse.cn",403);
            Check(registry.Snapshot().Single(x=>x.Source=="SZSE").BlockedUtc>=clock.GetUtcNow().AddHours(6),"403至少六小时冷却");
            var until=clock.GetUtcNow().AddDays(4);
            await Denied(registry.AcquireAsync("legacy-plugin","etf.sse.com.cn",new(clock.GetUtcNow(),until,7),default),"SourceCooldown");
            Check(registry.Snapshot().Single(x=>x.Source=="SSE").BlockedUtc==until,"旧插件冷却只延长共享限制");
        }
        await QueueStorm(Path.Combine(root,"storm"),key,clock,Check,Denied);
        await GatewayIntegration(Path.Combine(root,"http"),key,Check);
        await GatewayLocalFailure(Path.Combine(root,"local-failure"),key,Check);
        await RateSpacing(Path.Combine(root,"rate"),key,Check);
        StorageFailure(Path.Combine(root,"storage-failure"),key,Check);
        await SourceBudgetRecoveryChecks.RunAsync(Path.Combine(root,"recovery"),key,Check);
        using(var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key),clock))
        using(var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler-config.db"),key))
        await using(var catalog=new BackgroundPluginCatalog())
        await using(var runtime=new BackgroundRuntime(repository,catalog,sources:registry))
        {
            var response=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),new("test",false),default);
            Check(response.Health?.Sources?.Single(x=>x.Source=="SSE").State=="Cooldown","Worker只读健康响应包含持久来源状态");
        }
        var locked=false;
        try
        {
            using var plain=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"source-budgets.db"),Pooling=false}.ToString());
            plain.Open();using var command=plain.CreateCommand();command.CommandText="SELECT * FROM source_budget";command.ExecuteScalar();
        }
        catch(SqliteException){locked=true;}
        Check(locked,"宿主预算库无钥拒读");
        var broken=Path.Combine(root,"broken");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"source-budgets.db"),"broken");
        try{using var ignored=new SourceBudgetRegistry(new SqliteSourceBudgetStore(broken,key));throw new Exception("损坏被重建");}
        catch(SqliteException){Check(true,"损坏库失败关闭，不清除冷却重建");}
        CryptographicOperations.ZeroMemory(key);
        Console.WriteLine($"共享来源预算通过：{passed}项。现场：{root}");
    }
    private static async Task QueueStorm(string root,byte[] key,Clock clock,Action<bool,string> check,Func<Task<IBackgroundSourceLease>,string,Task> denied)
    {
        using var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key),clock);
        using var active=await registry.AcquireAsync("active","www.sse.com.cn",Empty,default);
        using var cancel=new CancellationTokenSource();
        var tasks=Enumerable.Range(0,32).Select(i=>registry.AcquireAsync("plugin-"+(i/8),"www.sse.com.cn",Empty,cancel.Token)).ToArray();
        check(registry.Snapshot().Single(x=>x.Source=="SSE").Waiting==32,"跨插件过载队列有界32项");
        await denied(registry.AcquireAsync("overflow","query.sse.com.cn",Empty,default),"SourceQueueFull");
        cancel.Cancel();
        foreach(var task in tasks)try{using var ignored=await task;}catch(OperationCanceledException){}
        check(registry.Snapshot().Single(x=>x.Source=="SSE").Waiting==0,"取消风暴释放全部等待项");
        var pluginTasks=Enumerable.Range(0,8).Select(_=>registry.AcquireAsync("single-plugin","query.sse.com.cn",Empty,default)).ToArray();
        await denied(registry.AcquireAsync("single-plugin","query.sse.com.cn",Empty,default),"SourceQueueFull");
        active.Complete(429);active.Dispose();
        foreach(var task in pluginTasks)await denied(task,"SourceCooldown");
        check(registry.Snapshot().Single(x=>x.Source=="SSE").Waiting==0,"拒绝后整队失败关闭，不形成重复网络风暴");
    }
    private static async Task GatewayIntegration(string root,byte[] key,Action<bool,string> check)
    {
        using var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key));
        var first=new Handler(HttpStatusCode.TooManyRequests);
        var second=new Handler(HttpStatusCode.OK);
        using var a=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"a")),first,new WorkerPluginContext("a",root,key,sources:registry));
        using var b=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"b")),second,new WorkerPluginContext("b",root,key,sources:registry));
        check((await a.GetAsync(new("https://query.sse.com.cn/test"),default)).StatusCode==429,"真实网关登记429并保留响应状态");
        try{await b.GetAsync(new("https://etf.sse.com.cn/test"),default);throw new Exception("跨插件未阻断");}
        catch(SourceBudgetUnavailableException){check(second.Calls==0,"第二插件私有策略为空也不再发出HTTP");}
        check((await b.GetAsync(new("https://www.szse.cn/test"),default)).StatusCode==200,"共享冷却不阻断另一交易所网关");
        b.RejectContract(new("https://www.szse.cn/test"));
        check(registry.Snapshot().Single(x=>x.Source=="SZSE").State=="Cooldown","正文契约校验失败反馈到宿主");
        using var old=new SourceRequestPolicy(Path.Combine(root,"legacy"));
        old.Failure("www.chinacnd.com",DateTimeOffset.UtcNow,403,DateTimeOffset.UtcNow.AddDays(5));
        using var legacy=new OfficialHttpGateway(old,new Handler(HttpStatusCode.OK),new WorkerPluginContext("legacy",root,key,sources:registry));
        check(registry.Snapshot().Single(x=>x.Source=="ISSUER-600153").BlockedUtc>DateTimeOffset.UtcNow.AddDays(4),"网关初始化即合并全部旧冷却，不等待首次请求");
        try{await legacy.GetAsync(new("https://www.chinacnd.com/report-227.html"),default);throw new Exception();}
        catch(SourceBudgetUnavailableException){check(registry.Snapshot().Single(x=>x.Source=="ISSUER-600153").BlockedUtc>DateTimeOffset.UtcNow.AddDays(4),"实际旧策略冷却先合并到宿主再拒绝");}
    }
    private static async Task RateSpacing(string root,byte[] key,Action<bool,string> check)
    {
        using var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key));
        var watch=System.Diagnostics.Stopwatch.StartNew();
        using(var first=await registry.AcquireAsync("one","www.sse.com.cn",Empty,default))first.Complete(200);
        using(var second=await registry.AcquireAsync("two","query.sse.com.cn",Empty,default))
            check(watch.Elapsed>=TimeSpan.FromMilliseconds(2900),"真实时钟验证跨插件至少三秒请求间隔");
    }
    private static async Task GatewayLocalFailure(string root,byte[] key,Action<bool,string> check)
    {
        using var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key));
        var local=Path.Combine(root,"local");
        var policy=new SourceRequestPolicy(local);policy.Reserve("www.sse.com.cn",DateTimeOffset.UtcNow.AddMinutes(-1));
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(local,"source-policy.db"),Pooling=false}.ToString());
        db.Open();using var command=db.CreateCommand();
        command.CommandText="CREATE TRIGGER fail_local BEFORE UPDATE ON source_policy WHEN NEW.failures>0 BEGIN SELECT RAISE(ABORT,'injected'); END";command.ExecuteNonQuery();
        using var gateway=new OfficialHttpGateway(policy,new Handler(HttpStatusCode.TooManyRequests),new WorkerPluginContext("one",root,key,sources:registry));
        try{await gateway.GetAsync(new("https://query.sse.com.cn/test"),default);throw new Exception("插件故障未上报");}
        catch(IOException error) when(error.InnerException is SqliteException){check(registry.Snapshot().Single(x=>x.Source=="SSE").State=="Cooldown","插件策略写失败仍先保留宿主429冷却");}
    }
    private static void StorageFailure(string root,byte[] key,Action<bool,string> check)
    {
        using var registry=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key));
        var derived=HKDF.DeriveKey(HashAlgorithmName.SHA256,key,32,info:Encoding.UTF8.GetBytes("CRS.Worker.SourceBudget/v1"));
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"source-budgets.db"),Password=Convert.ToHexString(derived),Pooling=false}.ToString());
        CryptographicOperations.ZeroMemory(derived);db.Open();
        registry.Import("www.sse.com.cn",new(DateTimeOffset.UtcNow,DateTimeOffset.MinValue,0));
        using var command=db.CreateCommand();
        command.CommandText="CREATE TRIGGER fail_budget BEFORE UPDATE ON source_budget BEGIN SELECT RAISE(ABORT,'injected'); END";command.ExecuteNonQuery();
        try{registry.ReportFailure("www.sse.com.cn",429);throw new Exception("故障写入意外成功");}
        catch(IOException error) when(error.InnerException is SqliteException){check(true,"冷却写入失败以数据库无关异常向上报告");}
        command.CommandText="DROP TRIGGER fail_budget";command.ExecuteNonQuery();
        try{registry.Import("www.szse.cn",Empty);throw new Exception("故障后继续放行");}
        catch(InvalidDataException){check(true,"写入故障后即使存储恢复也保持失败关闭");}
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan value)=>now+=value;
    }
    private sealed class Handler(HttpStatusCode status):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;var response=new HttpResponseMessage(status){Content=new StringContent("test")};
            if(status==HttpStatusCode.TooManyRequests)response.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromDays(2));
            return Task.FromResult(response);
        }
    }
}
