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
        var root=Path.Combine(Path.GetTempPath(),"CRS.MarketQueryReliability",Guid.NewGuid().ToString("N"));
        var key=RandomNumberGenerator.GetBytes(32);var rawPath=Path.Combine(root,"raw");var data=Path.Combine(root,"data");
        using var entered=new ManualResetEventSlim();var wait=0;
        var raw=new RawEvidenceStore(rawPath,openRead:path=>Volatile.Read(ref wait)==1?new WaitingStream(entered):File.OpenRead(path));
        using var repository=new MarketRepository(data,raw,encryptionKey:key);
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
        using(var writer=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Password=Convert.ToHexString(key),Pooling=false}.ToString()))
        {
            writer.Open();using(var check=writer.CreateCommand())
            {check.CommandText="SELECT count(*) FROM quality_issue WHERE rule_id='Q15' AND status='Open'";
                if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new Exception("完整巡检遗漏无关坏原件。");}
            using var transaction=writer.BeginTransaction();
            var timer=Stopwatch.StartNew();repository.ExecuteOperation(()=>repository.Dashboard(null),default);
            if(timer.Elapsed>TimeSpan.FromSeconds(1))throw new Exception("整页只读查询仍等待已有写锁。");
        }
        Console.WriteLine("PASS 整页只读事务不抢占已有写锁。");
        await using var workspace=new EtfWorkspace(new Context(),()=>repository,raw);
        await workspace.InitializeAsync(default);workspace.SetMarketActivitySource(new Source(Value));
        using var stop=new CancellationTokenSource();Volatile.Write(ref wait,1);
        var query=workspace.MarketActivityAsync(stop.Token);
        if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("查询未进入原件读取。");
        var job=workspace.SyncMarketActivityReportAsync(day.AddDays(1),day.AddDays(1),default);
        Volatile.Write(ref wait,0);var elapsed=Stopwatch.StartNew();stop.Cancel();
        try{await query.WaitAsync(TimeSpan.FromMilliseconds(800));throw new Exception("取消查询返回了部分结果。");}
        catch(OperationCanceledException){}
        var cancelledMs=elapsed.ElapsedMilliseconds;
        var completed=await job.WaitAsync(TimeSpan.FromSeconds(5));
        if(completed is not{ReceivedRows:2,SourceIssues:0}||(await workspace.MarketActivityAsync(default)).Count!=2)
            throw new Exception("查询取消后采集或后续查询不能继续。");
        Console.WriteLine($"PASS 真实仓储读取取消及并行采集：取消{cancelledMs}ms，采集提交2条，下一次查询成功。");
        CryptographicOperations.ZeroMemory(key);
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
