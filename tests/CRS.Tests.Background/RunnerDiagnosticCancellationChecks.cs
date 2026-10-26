using System.Diagnostics;
using CRS.Worker.Abstractions;
using CRS.Worker.PluginHost;

/// <summary>真实子进程和受控慢诊断；只使用隔离目录、自有密钥及合成能力，不接触正式Worker或源站。</summary>
internal static class RunnerDiagnosticCancellationChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var package=WorkerRemediationChecks.Package(root,("crs.diagnostic","IsolatedCapabilityEntry"));
        var trusted=System.Text.Json.JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(package.Trust))!.Single();
        var data=Path.Combine(root,"data");Directory.CreateDirectory(data);var context=new Context(data);
        using var releaseLog=new ManualResetEventSlim();var logEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy=new IsolatedPluginSession(Path.Combine(AppContext.BaseDirectory,"Runner","CRS.Worker.PluginRunner.exe"),package.Root,package.Trust,trusted,context,
            (_,phase,code)=>{if(phase=="Query"&&code=="CallerCanceled"){logEntered.TrySetResult();releaseLog.Wait(TimeSpan.FromSeconds(3));}});
        await proxy.StartAsync(default);var pid=await proxy.QueryAsync("pid",default);var hold=Path.Combine(data,"diagnostic-job.hold");File.WriteAllText(hold,"synthetic");
        var jobEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var work=proxy.ExecuteWithProgressAsync("one","hold",_=>{jobEntered.TrySetResult();return Task.CompletedTask;},default,Guid.NewGuid());
        var passed=0;void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 诊断取消隔离："+label);}
        try
        {
            await jobEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(context.Active==1,"同一真实Runner的后台作业已持有合成来源租约");
            using var stop=new CancellationTokenSource();var query=proxy.QueryAsync("cooperative-cancel",stop.Token);var end=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(data,"diagnostic-query.entered"))){if(end.Elapsed>TimeSpan.FromSeconds(5))throw new TimeoutException();await Task.Delay(10);}
            stop.Cancel();await logEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 诊断消费者仍被显式挡住；查询必须正常回报取消，不得等待诊断而杀死其它作业。
            try{await query.WaitAsync(TimeSpan.FromSeconds(2));throw new Exception("取消被报告成功。");}catch(OperationCanceledException){}
            Check(!releaseLog.IsSet&&context.Active==1,"慢诊断未把可协作查询取消升级为结果未知或终止后台作业");
            Check(await proxy.QueryAsync("pid",default).WaitAsync(TimeSpan.FromSeconds(2))==pid,"诊断仍拥塞时同一插件PID继续接受只读查询");
            await Task.Delay(2000);
            Check(context.Active==1&&await proxy.QueryAsync("pid",default)==pid,"诊断RPC本身超时后停发诊断，业务通道和后台作业仍保留");
            releaseLog.Set();File.Delete(hold);var done=await work.WaitAsync(TimeSpan.FromSeconds(5));
            Check(done.Status=="Succeeded"&&context.Active==0&&context.Notifications==1,"原后台作业沿原执行完成一次，来源额度归还且合成通知不重复");
            Check(await proxy.QueryAsync("pid",default)==pid&&proxy.FailureCode is null,"全过程没有重启或进入Runner故障冷却");
            await BufferFailureBoundary(Check);
            Console.WriteLine($"PASS 诊断取消隔离合计{passed}；不代表正式环境已修复。");
        }
        finally
        {
            releaseLog.Set();if(File.Exists(hold))File.Delete(hold);
            try{await work.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception){}
        }
    }
    private static async Task BufferFailureBoundary(Action<bool,string> check)
    {
        // 直接驱动实际Runner诊断组件，检查失败预算和关闭边界，不在正式进程注入日志延迟。
        var assembly=System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"Runner","CRS.Worker.PluginRunner.dll"));
        var type=assembly.GetType("CRS.Worker.PluginRunner.BufferedRunnerDiagnostics",true)!;
        var constructor=type.GetConstructors(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Single();
        var calls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var buffer=(IAsyncDisposable)constructor.Invoke([new Func<RunnerLifecycle,CancellationToken,Task>((_,_)=>{Interlocked.Increment(ref calls);entered.TrySetResult();return Task.FromException(new IOException("synthetic diagnostic transport failure"));})]))
        {
            type.GetMethod("Start")!.Invoke(buffer,null);
            for(var i=0;i<1000;i++)type.GetMethod("Report")!.Invoke(buffer,["Query","ElapsedMs=123"]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(100);
            check(calls==1,"首个诊断传输结果未知后不继续堆积远端调用，业务RPC预算不被日志耗尽");
        }
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked=(IAsyncDisposable)constructor.Invoke([new Func<RunnerLifecycle,CancellationToken,Task>((_,_)=>{entered.TrySetResult();return release.Task;})]);
        type.GetMethod("Start")!.Invoke(blocked,null);type.GetMethod("Report")!.Invoke(blocked,["Query","ElapsedMs=123"]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var timer=Stopwatch.StartNew();try{await blocked.DisposeAsync();check(timer.Elapsed<TimeSpan.FromSeconds(3),"不响应取消的诊断消费者也不能无限阻塞Runner停止");}finally{release.TrySetResult();}
    }
    private sealed class Context(string directory):IBackgroundHostContext,IBackgroundSourceContext,IBackgroundNotificationContext
    {
        public int Active,Notifications;public string PluginId=>"crs.diagnostic";public string DataDirectory=>directory;
        public byte[] OpenDataKey()=>Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();
        public void ImportSourceState(string host,SourceBudgetState state){}
        public void ReportSourceFailure(string host,int status,DateTimeOffset? retryAfter=null){}
        public Task<IBackgroundSourceLease> AcquireSourceAsync(string host,SourceBudgetState state,CancellationToken token)
        {token.ThrowIfCancellationRequested();Interlocked.Increment(ref Active);return Task.FromResult<IBackgroundSourceLease>(new Lease(this));}
        public Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<BackgroundNotificationChannel>>([new("fixture","synthetic",true,true)]);
        public Task<BackgroundNotificationResult> SendNotificationAsync(string channel,string profile,BackgroundNotification message,CancellationToken token)
        {Interlocked.Increment(ref Notifications);return Task.FromResult(new BackgroundNotificationResult(channel,"Accepted","SyntheticOnly",message.MessageId));}
        private sealed class Lease(Context owner):IBackgroundSourceLease
        {private int ended;public void Complete(int status,DateTimeOffset? until=null){}public void Dispose(){if(Interlocked.Exchange(ref ended,1)==0)Interlocked.Decrement(ref owner.Active);}}
    }
}
