using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class MarketQueryReliabilityChecks
{
    public static async Task RunAsync()
    {
        await WriterAdmissionDoesNotBlockRead();
        var root=Path.Combine(Path.GetTempPath(),"CRS.MarketQueryReliability",Guid.NewGuid().ToString("N"));
        var key=RandomNumberGenerator.GetBytes(32);var rawPath=Path.Combine(root,"raw");var data=Path.Combine(root,"data");
        using var entered=new ManualResetEventSlim();var wait=0;
        var raw=new RawEvidenceStore(rawPath,openRead:path=>Volatile.Read(ref wait)==1?new WaitingStream(entered):File.OpenRead(path));
        using var repository=new MarketRepository(data,raw,approvalAuditKey:key);
        var day=new DateOnly(2026,10,13);
        MarketActivitySource Value(string market,DateOnly date)=>new(OfficialMarketActivitySource.Request(market,date),
            Encoding.UTF8.GetBytes($"synthetic-{market}-{date}"),market,date,new(market,date,100,200));
        repository.SaveMarketActivity("SSE",day,Value("SSE",day));repository.SaveMarketActivity("SZSE",day,Value("SZSE",day));
        // 与成交无关的未登记坏原件不再拖累成交页；完整巡检仍必须发现它。
        var unrelated=raw.Save(Encoding.UTF8.GetBytes("unrelated-disclosure"));
        repository.RecordFetch(unrelated,"https://www.sse.com.cn/disclosure.pdf",unrelated,"Disclosure","test");
        File.WriteAllText(Path.Combine(rawPath,unrelated+".raw"),"tampered");
        if(repository.MarketActivity().Single().AmountYuan!=200||repository.MarketActivityRevisions().Count!=2)
            throw new Exception("成交查询错误依赖无关披露证据。");
        repository.ReconcileEvidenceIntegrity();
        Console.WriteLine("PASS 成交查询仅核验相关证据；完整巡检仍发现无关披露原件损坏。");
        // WAL已有其他写入者时，整页只读事务不能抢占写锁。
        using(var writer=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString()))
        {
            writer.Open();using(var check=writer.CreateCommand())
            {check.CommandText="SELECT count(*) FROM quality_issue WHERE rule_id='Q15' AND status='Open'";
                if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new Exception("完整巡检遗漏无关坏原件。");}
            using var transaction=writer.BeginTransaction();
            var timer=Stopwatch.StartNew();repository.ExecuteOperation(()=>repository.Dashboard(null),default);
            if(timer.Elapsed>TimeSpan.FromSeconds(1))throw new Exception("整页只读查询仍等待已有写锁。");
        }
        Console.WriteLine("PASS 整页只读事务不抢占已有写锁。");
        await using var workspace=new NationalTeamApplication(new Context(),()=>repository,raw);
        await workspace.InitializeAsync(default);workspace.Activity.SetMarketActivitySource(new Source(Value));
        using var stop=new CancellationTokenSource();Volatile.Write(ref wait,1);
        var query=workspace.Activity.MarketActivityAsync(stop.Token);
        if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("查询未进入原件读取。");
        var job=workspace.Activity.SyncMarketActivityReportAsync(day.AddDays(1),day.AddDays(1),default);
        Volatile.Write(ref wait,0);var elapsed=Stopwatch.StartNew();stop.Cancel();
        try{await query.WaitAsync(TimeSpan.FromMilliseconds(800));throw new Exception("取消查询返回了部分结果。");}
        catch(OperationCanceledException){}
        var cancelledMs=elapsed.ElapsedMilliseconds;
        var completed=await job.WaitAsync(TimeSpan.FromSeconds(5));
        if(completed is not{ReceivedRows:2,SourceIssues:0}||(await workspace.Activity.MarketActivityAsync(default)).Count!=2)
            throw new Exception("查询取消后采集或后续查询不能继续。");
        Console.WriteLine($"PASS 真实仓储读取取消及并行采集：取消{cancelledMs}ms，采集提交2条，下一次查询成功。");
        // 端口不再经过写门槛后，仍须由读取生命周期保护仓储；关闭等待本次读取结束，而非提前关连接。
        entered.Reset();Volatile.Write(ref wait,1);using var closeRead=new CancellationTokenSource();
        var activeRead=workspace.Activity.MarketActivityAsync(closeRead.Token);
        if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("关闭检查未进入真实原件读取。");
        var closing=workspace.DisposeAsync().AsTask();await Task.Delay(50);
        if(closing.IsCompleted)throw new Exception("读取尚未结束即关闭仓储。");
        closeRead.Cancel();try{await activeRead;throw new Exception("关闭前取消返回了部分结果。");}catch(OperationCanceledException){}
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
        try{await workspace.Etf.ConfigurationAsync(default);throw new Exception("关闭后仍接收读取。");}catch(ObjectDisposedException){}
        Console.WriteLine("PASS 关闭等待在途独立读取，取消释放后可关闭，关闭后拒绝新读取。");
        CryptographicOperations.ZeroMemory(key);
    }
    private static async Task WriterAdmissionDoesNotBlockRead()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.MarketReadAdmission",Guid.NewGuid().ToString("N"));
        var key=RandomNumberGenerator.GetBytes(32);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var hold=0;
        // 阻塞真实写用例的准入后阶段，而非网络下载；旧实现会让独立只读连接仍排在写门槛之后。
        using var repository=new MarketRepository(Path.Combine(root,"data"),raw,()=>
        {
            if(Volatile.Read(ref hold)==1){entered.Set();if(!release.Wait(TimeSpan.FromSeconds(15)))throw new TimeoutException("隔离写入未释放。");}
        },approvalAuditKey:key);
        await using var workspace=new NationalTeamApplication(new Context(),()=>repository,raw);
        await workspace.InitializeAsync(default);Volatile.Write(ref hold,1);
        var writer=workspace.Etf.SaveConfigurationAsync(new("SSE","510300"),"独立读取检查","测试指数",true,"隔离并发检查",0,default);
        try
        {
            if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("写用例未持有准入门槛。");
            // 验证读不等待写的因果关系，给SQLCipher新连接在并行回归下留预算；不是一秒性能承诺，旧写门槛仍会等到超时。
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var rows=await workspace.Etf.ConfigurationAsync(stop.Token);
            if(writer.IsCompleted||rows.Single(x=>x.Ticker=="510300").Name=="独立读取检查")throw new Exception("读取混入尚未提交的配置。");
        }
        finally{Volatile.Write(ref hold,0);release.Set();await writer;CryptographicOperations.ZeroMemory(key);}
        if((await workspace.Etf.ConfigurationAsync(default)).Single(x=>x.Ticker=="510300").Name!="独立读取检查")throw new Exception("写入提交后新快照未见更新。");
        Console.WriteLine("PASS 独立读快照不等待应用写门槛，未提交配置不可见、提交后下一次读取可见。");
    }
    private sealed class Context:IPluginHostContext
    {
        public string PluginId=>"crs.national-team";
        public IPluginJobScheduler Jobs=>throw new NotSupportedException();public IPluginDataPaths DataPaths=>throw new NotSupportedException();
        public IPluginHttpGateway Http=>throw new NotSupportedException();public IPluginDiagnostics Diagnostics=>throw new NotSupportedException();
    }
    private sealed class Source(Func<string,DateOnly,MarketActivitySource> value):IMarketActivitySource
    {public Task<MarketActivitySource> FetchAsync(string market,DateOnly day,CancellationToken token)=>Task.FromResult(value(market,day));}
    private sealed class WaitingStream(ManualResetEventSlim entered):MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {entered.Set();await Task.Delay(Timeout.Infinite,token);return 0;}
    }
}
