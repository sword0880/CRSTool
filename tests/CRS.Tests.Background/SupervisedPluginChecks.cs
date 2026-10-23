using System.Security.Principal;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

/// <summary>使用真实子进程与SQLCipher仓储验证故障范围；不接触已安装服务或真实网络。</summary>
internal static class SupervisedPluginChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS 插件监督："+text);}
        var package=WorkerRemediationChecks.Package(root,("crs.good","IsolatedCapabilityEntry"),("crs.bad","IsolatedCapabilityEntry"),("crs.start-hang","HangingStartEntry"));
        var contexts=new Dictionary<string,Context>();var proxies=new Dictionary<string,IsolatedPluginSession>();
        var runner=Path.Combine(AppContext.BaseDirectory,"Runner","CRS.Worker.PluginRunner.exe");
        var clock=new RestartClock();
        await using var catalog=new BackgroundPluginCatalog(lifecycleTimeout:TimeSpan.FromSeconds(5),processSessions:p=>
        {
            var context=new Context(p.Id,Path.Combine(root,p.Id));contexts.Add(p.Id,context);
            var proxy=new IsolatedPluginSession(runner,package.Root,package.Trust,p,context,
                (id,phase,code)=>Console.WriteLine("DIAGNOSTIC "+id+" "+phase+" "+code),clock);proxies.Add(p.Id,proxy);return proxy;
        });
        await catalog.LoadAsync(package.Root,package.Trust,default);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(catalog.Statuses));
        Check(catalog.Statuses.Count(x=>x.State=="Started")==2&&catalog.Statuses.Any(x=>x.PluginId=="crs.start-hang"&&x.State=="Failed"),"启动卡死只隔离该插件，其余两个插件已启动");
        Check(!catalog.RequiresProcessRestart,"卡死Runner未使整个Catalog进入进程重启状态");
        var healthy=await catalog.QueryAsync("crs.good","pid",default);var faulty=await catalog.QueryAsync("crs.bad","pid",default);
        Check(healthy!=faulty&&!healthy.EndsWith(":"+Environment.ProcessId),"两个插件和Worker分别运行在不同PID");
        // 即使来自另一个Worker监督器，相同插件目录也必须保持单一写入进程。
        var trusted=System.Text.Json.JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(package.Trust))!.Single(x=>x.Id=="crs.good");
        await using(var duplicate=new IsolatedPluginSession(runner,package.Root,package.Trust,trusted,contexts["crs.good"]))
        {
            using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(400));var rejected=false;
            try{await duplicate.StartAsync(cancel.Token);}catch(Exception error) when(error is OperationCanceledException or IOException){rejected=true;}
            Check(rejected&&await catalog.QueryAsync("crs.good","pid",default)==healthy,"跨监督器第二实例不能同时取得插件目录租约，原实例继续服务");
        }
        try{await catalog.QueryAsync("crs.good","reject",default);throw new Exception("普通业务拒绝未返回失败");}catch(InvalidDataException){}
        Check(await catalog.QueryAsync("crs.good","pid",default)==healthy,"证据类业务拒绝不会重启正在服务的Runner");
        var progress=0;var command=Guid.NewGuid();
        var result=await proxies["crs.good"].ExecuteWithProgressAsync("one","ok",_=>{progress++;return Task.CompletedTask;},default,command);
        Check(result.Status=="Succeeded"&&result.Checkpoint=="command="+command.ToString("N")&&progress==1,"命令ID、实时进度、来源租约和通知经过双向能力代理");
        Check(contexts["crs.good"].Active==0&&contexts["crs.good"].Notifications==1,"成功执行恰好归还额度并提交一次合成通知");
        contexts["crs.good"].RejectCode="SourceCooldown";
        try{await proxies["crs.good"].ExecuteAsync("one","ok",default);throw new Exception("来源冷却未传回");}
        catch(SourceBudgetUnavailableException error){Check(error.Code=="SourceCooldown"&&error.Until==Context.CooldownUntil,"来源冷却分类与实际截止时间经双向协议完整保留");}
        finally{contexts["crs.good"].RejectCode=null;}
        // 让坏插件持有来源额度后忽略取消；好插件同时继续提供查询。
        using(var cancel=new CancellationTokenSource())
        {
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work=proxies["crs.bad"].ExecuteWithProgressAsync("one","hang",_=>{entered.TrySetResult();return Task.CompletedTask;},cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(contexts["crs.bad"].Active==1,"失控执行确实持有宿主来源额度");
            cancel.Cancel();
            try{await work;throw new Exception("失控任务被报为成功");}catch(RunnerOutcomeUnknownException){}
        }
        Check(contexts["crs.bad"].Active==0,"确认子进程终止后回收失控执行的来源额度");
        Check(await catalog.QueryAsync("crs.good","pid",default)==healthy&&!catalog.RequiresProcessRestart,"坏插件终止后好插件保持原PID且继续查询");
        // 只推进重启预算的单调时钟，真实进程退出和RPC取消仍使用真实时间。
        clock.Advance();var restarted=await catalog.QueryAsync("crs.bad","pid",default);
        Check(restarted!=faulty,"旧Runner确认退出后可在预算内重新启动同一插件");
        try{await catalog.QueryAsync("crs.bad","crash",default);throw new Exception("子进程崩溃未报告");}catch(RunnerOutcomeUnknownException){}
        clock.Advance();_=await catalog.QueryAsync("crs.bad","pid",default);
        try{await catalog.QueryAsync("crs.bad","crash",default);throw new Exception("子进程崩溃未报告");}catch(RunnerOutcomeUnknownException){}
        clock.Advance();
        try{await catalog.QueryAsync("crs.bad","pid",default);throw new Exception("超出重启预算仍启动");}catch(IOException){}
        Check(proxies["crs.bad"].FailureCode=="RunnerRestartBudgetExhausted"&&!catalog.CanDispatch("crs.bad"),"十分钟三次启动预算耗尽后禁止该插件继续分派");
        Check(await catalog.QueryAsync("crs.good","pid",default)==healthy,"连续崩溃及熔断不改变其它插件PID");
        // 使用真实运行时检查持久命令完成态，不能只验证RPC返回。
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"tasks.db"),Enumerable.Range(1,32).Select(x=>(byte)x).ToArray());
        await using var runtime=new BackgroundRuntime(repository,catalog);
        await runtime.StartAsync(default);
        var caller=new BackgroundCaller(WindowsIdentity.GetCurrent().User!.Value,true);
        var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.good","one",Payload:"ok");
        var accepted=await runtime.HandleAsync(request,caller,default);Check(accepted.Code=="Accepted","Worker持久受理命令");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));CommandReceipt? receipt;
        do
        {
            var response=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),caller,deadline.Token);
            receipt=response.Receipt;if(receipt?.State!="Completed")await Task.Delay(25,deadline.Token);
        }while(receipt?.State!="Completed");
        Check(receipt.Code=="Succeeded"&&receipt.ExecutionId is not null,"真实Worker执行通过Runner完成并保存最终Succeeded回执");
        var record=repository.FindExecution(receipt.ExecutionId!);
        Check(record?.Checkpoint=="command="+request.RequestId.ToString("N")&&record.Progress?.Count==1,"执行历史绑定原持久命令且实时阶段已入库");
        var crashRequest=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.good","one",Payload:"hang");
        Check((await runtime.HandleAsync(crashRequest,caller,default)).Code=="Accepted","持久受理即将发生子进程崩溃的执行");
        while(contexts["crs.good"].Active==0)await Task.Delay(25,deadline.Token);
        try{await proxies["crs.good"].QueryAsync("crash",default);throw new Exception("未触发真实崩溃");}catch(RunnerOutcomeUnknownException){}
        do
        {
            var response=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:crashRequest.RequestId),caller,deadline.Token);
            receipt=response.Receipt;if(receipt?.State!="Completed")await Task.Delay(25,deadline.Token);
        }while(receipt?.State!="Completed");
        record=repository.FindExecution(receipt.ExecutionId!);
        Check(receipt.Code=="Failed"&&record?.Checkpoint=="RunnerOutcomeUnknown"&&contexts["crs.good"].Active==0,
            "执行中真实崩溃保存Failed和结果未知标记，回收额度，不伪报Succeeded");
        var health=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),caller,default);
        Check(health.Code=="Ok"&&health.Health?.ProcessId==Environment.ProcessId&&health.Health.State=="Degraded","故障仅使健康显示降级，Worker原进程继续响应");
        var stopPackage=WorkerRemediationChecks.Package(root,("crs.stop-hang","HangingStopEntry"));
        await using(var stopCatalog=new BackgroundPluginCatalog(processSessions:p=>new IsolatedPluginSession(runner,stopPackage.Root,stopPackage.Trust,p,
            new Context(p.Id,Path.Combine(root,p.Id)))))
        {
            await stopCatalog.LoadAsync(stopPackage.Root,stopPackage.Trust,default);
            await stopCatalog.DisposeAsync();
            Check(!stopCatalog.RequiresProcessRestart,"停止忽略取消时只终止其Runner，宿主无需退出");
        }
        Console.WriteLine($"插件监督隔离验证通过：{passed}项。");
    }
    private sealed class RestartClock:TimeProvider
    {
        private long timestamp=1;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>Interlocked.Read(ref timestamp);
        public void Advance()=>Interlocked.Add(ref timestamp,11000);
    }
    private sealed class Context(string id,string directory):IBackgroundHostContext,IBackgroundSourceContext,IBackgroundNotificationContext
    {
        public static readonly DateTimeOffset CooldownUntil=new(2026,10,24,0,0,0,TimeSpan.FromHours(8));
        public string? RejectCode;
        public int Active,Notifications;public string PluginId=>id;public string DataDirectory=>directory;
        public byte[] OpenDataKey()=>Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();
        public void ImportSourceState(string host,SourceBudgetState legacy){}
        public void ReportSourceFailure(string host,int status,DateTimeOffset? retryAfter=null){}
        public Task<IBackgroundSourceLease> AcquireSourceAsync(string host,SourceBudgetState legacy,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(RejectCode is {} code)throw new SourceBudgetUnavailableException(code,CooldownUntil);
            Interlocked.Increment(ref Active);return Task.FromResult<IBackgroundSourceLease>(new Lease(this));}
        public Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)
            =>Task.FromResult<IReadOnlyList<BackgroundNotificationChannel>>([new("fixture","仅合成通知",true,true)]);
        public Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token)
        {Interlocked.Increment(ref Notifications);return Task.FromResult(new BackgroundNotificationResult(channelId,"Accepted","SyntheticOnly",message.MessageId));}
        private sealed class Lease(Context owner):IBackgroundSourceLease
        {private int disposed;public void Complete(int status,DateTimeOffset? until=null){}public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)Interlocked.Decrement(ref owner.Active);}}
    }
}
