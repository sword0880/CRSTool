using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class NationalTeamConcurrencyChecks
{
    private static void Check(bool value,string name)
    {if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS 国家队并发："+name);}
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.NationalTeamConcurrency",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            await Sources(Path.Combine(root,"sources"),key);
            await Sessions(Path.Combine(root,"session"),key);
            await Integrity(Path.Combine(root,"integrity"),key);
            var raw=new RawEvidenceStore(Path.Combine(root,"raw-race"));var bytes=Encoding.UTF8.GetBytes("synthetic common response");
            var hashes=await Task.WhenAll(Enumerable.Range(0,32).Select(_=>Task.Run(()=>raw.Save(bytes))));
            Check(hashes.Distinct().Count()==1&&raw.Verify(hashes[0]),"并行保存相同原件幂等，不产生覆盖或半文件");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static async Task Sources(string root,byte[] key)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int sse=0,szse=0;
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(root,key),new Handler(async(uri,token)=>
        {
            if(uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal))
            {Interlocked.Increment(ref sse);entered.TrySetResult();await release.Task.WaitAsync(token);}
            else Interlocked.Increment(ref szse);
            return Response("synthetic");
        }));
        var first=gateway.GetAsync(new("https://query.sse.com.cn/test"),default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop=new CancellationTokenSource();var alias=gateway.GetAsync(new("https://static.sse.com.cn/test"),stop.Token);
        try
        {
            var independent=await gateway.GetAsync(new("https://www.szse.cn/test"),default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(independent.StatusCode==200&&szse==1&&sse==1&&!first.IsCompleted&&!alias.IsCompleted,
                "SSE请求未结束时SZSE实际完成，同交易所域名仍共用一个额度");
            stop.Cancel();try{await alias;throw new Exception("排队取消失效。");}catch(OperationCanceledException){}
            gateway.RejectContract(new("https://query.sse.com.cn/test"));
            release.TrySetResult();await first;
            Check(gateway.RetryAt(new("https://etf.sse.com.cn/test"))>CRS.Timing.BeijingClock.Now&&sse==1,
                "迟到成功不清除并行反馈的来源冷却，排队取消不额外发HTTP");
        }
        finally{release.TrySetResult();stop.Cancel();}
    }
    private static async Task Sessions(string root,byte[] key)
    {
        Directory.CreateDirectory(root);
        var shareEntered=new ConcurrentDictionary<string,byte>();
        var both=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var day=new DateOnly(2026,10,14);var other=day.AddDays(1);
        using var gateway=new OfficialHttpGateway(new SourceRequestPolicy(Path.Combine(root,"policy"),key),new Handler(async(uri,token)=>
        {
            var query=Query(uri);
            var activity=query.GetValueOrDefault("sqlId")=="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C"||query.GetValueOrDefault("CATALOGID")=="scsj_gprdgk_after";
            if(!activity)
            {
                shareEntered.TryAdd(uri.Host,0);if(shareEntered.Count==2)both.TrySetResult();
                await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();
            }
            return Response(Activity(uri,query));
        }));
        var context=new Context(root,key);
        // 只保留两只合成测试ETF，仍使用真实加密仓储、会话和来源适配器。
        using(var seed=new MarketRepository(Path.Combine(root,"data"),new(Path.Combine(root,"raw")),encryptionKey:key))
        {
            foreach(var row in seed.Configuration().Where(x=>x.Ticker is not ("510300" or "159919")))
                seed.SaveConfiguration(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"synthetic",row.Revision);
        }
        await using var session=new NationalTeamBackgroundSession(context,gateway);await session.StartAsync(default);
        Check(session.MaximumConcurrentExecutions==4&&session.ExecutionGroup("market-activity-sync")==new BackgroundExecutionGroup("collection",2)
            &&session.ExecutionGroup("alert-evaluate")==new BackgroundExecutionGroup("evaluation",1),"真实会话声明采集2、预警1、管理1且总额4");
        var shareLog=new ConcurrentQueue<BackgroundProgress>();var activityLog=new ConcurrentQueue<BackgroundProgress>();
        using var cancelled=new CancellationTokenSource();
        var share=session.ExecuteWithProgressAsync("official-share-sync",JsonSerializer.Serialize(new EtfRequest("sync",From:other,To:other)),
            p=>{shareLog.Enqueue(p);return Task.CompletedTask;},cancelled.Token,Guid.NewGuid());
        await both.Task.WaitAsync(TimeSpan.FromSeconds(6));
        var activityRun=session.ExecuteWithProgressAsync("market-activity-sync",JsonSerializer.Serialize(new EtfRequest("market-sync",From:day,To:day)),
            p=>{activityLog.Enqueue(p);return Task.CompletedTask;},default,Guid.NewGuid());
        try
        {
            // 预警读取当前已提交事实，不等待两条仍在网络等待中的采集。
            var evaluation=await session.ExecuteAsync("alert-evaluate",default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(evaluation.Status=="Succeeded"&&!share.IsCompleted&&!activityRun.IsCompleted,"两采集尚未结束时预警在一致仓储操作内真实完成");
            cancelled.Cancel();try{await share;throw new Exception("份额取消失效。");}catch(OperationCanceledException){}
            var result=await activityRun.WaitAsync(TimeSpan.FromSeconds(8));
            Check(result.Status=="Succeeded"&&result.Checkpoint!.Contains("httpStarted=2;",StringComparison.Ordinal),
                "只取消份额执行，成交统计继续并提交，HTTP计数独立为2");
            Check(shareLog.Any(x=>x.Stage=="RequestStarted")&&activityLog.Any(x=>x.Stage=="RequestStarted")
                &&shareLog.Where(x=>x.Date is not null).All(x=>x.Date==other)
                &&activityLog.Where(x=>x.Date is not null).All(x=>x.Date==day),"并行任务的日期、HTTP阶段和进度回调归属互不串线");
            var points=JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("market-activity")),default));
            Check(points is not null,"并行采集后公开查询仍返回完整分页响应");
        }
        finally{cancelled.Cancel();try{await share;}catch(OperationCanceledException){}await activityRun;}
    }
    private static async Task Integrity(string root,byte[] key)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pause=0;
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>new PausingStream(File.OpenRead(path),
            ()=>Volatile.Read(ref pause)==1,entered,release));
        using var repository=new MarketRepository(Path.Combine(root,"data"),raw,encryptionKey:key);
        var hash=raw.Save(Encoding.UTF8.GetBytes("synthetic integrity evidence"));repository.RecordFetch(hash,"https://www.sse.com.cn/test",hash);
        await using var workspace=new EtfWorkspace(new WorkspaceContext(root),()=>repository,raw);await workspace.InitializeAsync(default);
        var row=(await workspace.ConfigurationAsync(default)).First();
        Volatile.Write(ref pause,1);var check=workspace.ReconcileEvidenceIntegrityAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var secondStop=new CancellationTokenSource();
            var second=workspace.ReconcileEvidenceIntegrityAsync(secondStop.Token);secondStop.Cancel();
            try{await second;throw new InvalidOperationException("第二巡检没有取消。");}catch(OperationCanceledException){}
            Check(!check.IsCompleted,"同一完整性巡检串行防止旧核验覆盖新结果，取消等待不影响原巡检");
            await workspace.SaveConfigurationAsync(new(row.Market,row.Ticker),"synthetic updated",row.IndexGroup,row.Active,"synthetic",row.Revision,default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Check(!check.IsCompleted&&(await workspace.ConfigurationAsync(default)).Single(x=>x.Ticker==row.Ticker).Name=="synthetic updated",
                "原件核验读取阻塞时短配置事务仍提交，不持市场库门槛等待文件");
            var stale=false;try{await workspace.SaveConfigurationAsync(new(row.Market,row.Ticker),row.Name,row.IndexGroup,row.Active,"stale",row.Revision,default);}
            catch(InvalidDataException){stale=true;}
            Check(stale,"并行配置修改仍拒绝旧修订，未绕过乐观并发校验");
        }
        finally{Volatile.Write(ref pause,0);release.TrySetResult();await check;}
    }
    private sealed class Context(string path,byte[] key):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>path;public byte[] OpenDataKey()=>key.ToArray();}
    private sealed class WorkspaceContext(string root):IPluginHostContext,IPluginDataPaths,IPluginJobScheduler,IPluginDiagnostics
    {
        public string PluginId=>"synthetic";public IPluginDataPaths DataPaths=>this;public IPluginJobScheduler Jobs=>this;
        public IPluginDiagnostics Diagnostics=>this;public IPluginHttpGateway Http=>throw new NotSupportedException();
        public string Data=>Path.Combine(root,"data");public string Raw=>Path.Combine(root,"raw");public string Log=>root;public string Backup=>root;
        public Task RunAsync(string id,Func<CancellationToken,Task> work,CancellationToken token)=>work(token);public void Report(string code,string message){}
    }
    private sealed class Handler(Func<Uri,CancellationToken,Task<HttpResponseMessage>> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>response(request.RequestUri!,token);}
    private static HttpResponseMessage Response(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static Dictionary<string,string> Query(Uri uri)=>uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
    private static string Activity(Uri uri,Dictionary<string,string> query)
    {
        var day=query.GetValueOrDefault("SEARCH_DATE")??query["txtQueryDate"];
        object content=uri.Host=="query.sse.com.cn"
            ?new{sqlId="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",actionErrors=Array.Empty<string>(),result=new[]{new{PRODUCT_CODE="17",TRADE_DATE=day.Replace("-",""),TRADE_AMT="100",TRADE_VOL="200"}}}
            :new[]{new{metadata=new{catalogid="scsj_gprdgk_after",tabkey="tab1",cols=new{gp="深市合计"},conditions=new[]{new{name="txtQueryDate",defaultValue=day}}},
                data=new[]{new{zbmc="成交金额（亿元）",gp="100"},new{zbmc="成交量（亿）",gp="200"}},error=(string?)null}};
        return JsonSerializer.Serialize(content);
    }
    private sealed class PausingStream(Stream inner,Func<bool> pause,TaskCompletionSource entered,TaskCompletionSource release):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>inner.CanSeek;public override bool CanWrite=>false;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(pause()){entered.TrySetResult();await release.Task.WaitAsync(token);}return await inner.ReadAsync(buffer,token);}
        public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);public override void Flush()=>inner.Flush();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
