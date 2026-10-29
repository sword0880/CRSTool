using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

/// <summary>公共类型、真实Runner错误协议及同一持久命令退避；只操作合成包和隔离数据库。</summary>
internal static class RunnerFailureChecks
{
    public static async Task RunAsync(string root)
    {
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("验收目录已存在。");Directory.CreateDirectory(root);
        var passed=0;void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS 准入错误边界："+message);}
        foreach(var isolated in new[]{false,true})
        {
            var mode=isolated?"runner":"in-process";var directory=Path.Combine(root,mode);Directory.CreateDirectory(directory);
            var package=Package(directory);IsolatedPluginSession? proxy=null;
            await using var catalog=new BackgroundPluginCatalog(processSessions:isolated?p=>proxy=new IsolatedPluginSession(Path.Combine(AppContext.BaseDirectory,"Runner","CRS.Worker.PluginRunner.exe"),package.Root,package.Trust,p):null);
            await catalog.LoadAsync(package.Root,package.Trust,default);
            Check(catalog.Statuses.Single().State=="Started",mode+"受信夹具启动");
            var pid=await catalog.QueryAsync("crs.admission","pid",default);
            // 先验证实际加载边界，而非只在默认加载上下文构造同类型异常。
            foreach(var reason in new[]{"Capacity","WaitBudget","Closed","ResearchCapacity","SnapshotCapacity","InputsChanged"})
            {
                Exception? observed=null;try{await catalog.QueryAsync("crs.admission","admit:"+reason,default);}catch(Exception error){observed=error;}
                File.AppendAllText(Path.Combine(directory,"observed.jsonl"),JsonSerializer.Serialize(new{reason,Type=observed?.GetType().FullName,AssemblyContext=observed is null?null:System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(observed.GetType().Assembly)?.Name})+Environment.NewLine);
                Check(observed is WorkAdmissionException admission&&admission.Reason==reason,mode+"准入原因跨加载/进程边界保留："+reason);
            }
            if(!isolated)
                using(ReadWorkloadScope.Enter(false))Check(await catalog.QueryAsync("crs.admission","scope",default)=="ResearchRead","公共执行上下文不因重复加载而分裂");
            if(isolated)
            {
                foreach(var pair in new[]{("io","IOException"),("timeout","TimeoutException"),("unknown","OperationFailed"),("admit:synthetic-private-reason","OperationFailed")})
                {
                    RunnerCallException? observed=null;try{await catalog.QueryAsync("crs.admission",pair.Item1,default);}catch(RunnerCallException error){observed=error;}
                    Check(observed?.Code==pair.Item2&&!observed.ToString().Contains("synthetic-private"),"远端仅返回固定错误码："+pair.Item2);
                }
                Check(new RunnerCallException("synthetic-private-reason").Code=="OperationFailed","未列入白名单的远端分类不能写入公开回执");
            }
            var session=(IParameterizedBackgroundSession)catalog.Jobs.Single().Session;
            var marker=Path.Combine(directory,"writer.hold");using var holdCancellation=new CancellationTokenSource();
            var holder=session.ExecuteAsync("one","hold:"+marker,holdCancellation.Token);
            var key=RandomNumberGenerator.GetBytes(32);using var repository=new EncryptedTaskRepository(Path.Combine(directory,"tasks.db"),key);CryptographicOperations.ZeroMemory(key);
            await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(55));var caller=new BackgroundCaller(WindowsIdentity.GetCurrent().User!.Value,true);
            async Task Until(Func<bool> ready){while(!ready())await Task.Delay(20,deadline.Token);}
            try
            {
                await Until(()=>File.Exists(marker));
                foreach(var reason in new[]{"ResearchCapacity","SnapshotCapacity"})
                    Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.admission",Payload:"admit:"+reason),caller,default)).Code=="PluginBusy",mode+"真实Worker将研究容量拒绝明确报告为繁忙："+reason);
                var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.admission","one",Payload:"write");
                Check((await runtime.HandleAsync(request,caller,default)).Code=="Accepted",mode+"真实写门槛被占用时命令仍先持久受理");
                await Until(()=>repository.History().Any(x=>x.Result!="Running"));
                var first=repository.History().Single();var receipt=repository.FindCommand(request.RequestId,caller.Sid);
                Check(first is{Result:"RetryDeferred",Checkpoint:"admission=WaitBudget"}&&receipt is{State:"Pending",Code:"RetryDeferred"},mode+"等待预算耗尽保留原命令待重试，不冒充成功或终态失败");
                Check(await catalog.QueryAsync("crs.admission","writes",default)=="0"&&await catalog.QueryAsync("crs.admission","pid",default)==pid,mode+"拒绝准入没有执行写入或重启插件");
                Check(repository.Plans().Single().Failures==0&&repository.Plans().Single().NotBefore>CRS.Timing.BeijingClock.Now,mode+"短期容量退避不增加业务失败次数且不会立即循环");
                File.Delete(marker);await holder.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(()=>repository.FindCommand(request.RequestId,caller.Sid)?.State=="Completed");
                receipt=repository.FindCommand(request.RequestId,caller.Sid);
                Check(receipt?.Code=="Succeeded"&&repository.History().Count==2&&repository.FindExecution(receipt.ExecutionId!)?.Checkpoint=="command="+request.RequestId.ToString("N"),mode+"自然退避后沿同一持久命令完成一次写入");
                Check(await catalog.QueryAsync("crs.admission","writes",default)=="1"&&await catalog.QueryAsync("crs.admission","pid",default)==pid,mode+"重试无重复副作用且保持同一插件进程");
                if(isolated)
                {
                    var failed=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.admission","one",Payload:"io");await runtime.HandleAsync(failed,caller,default);
                    await Until(()=>repository.FindCommand(failed.RequestId,caller.Sid)?.State=="Completed");
                    var failure=repository.FindCommand(failed.RequestId,caller.Sid)!;var record=repository.FindExecution(failure.ExecutionId!);
                    Check(failure.Code=="Failed"&&record?.Checkpoint=="runner=IOException"&&!JsonSerializer.Serialize(record).Contains("synthetic-private"),"正式仓储式回执保留远端I/O类别而不泄露异常消息");
                    var conflict=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.admission","one",Payload:"inputs-changed");await runtime.HandleAsync(conflict,caller,default);
                    await Until(()=>repository.FindCommand(conflict.RequestId,caller.Sid)?.Code=="RetryDeferred");
                    Check(repository.FindCommand(conflict.RequestId,caller.Sid)?.State=="Pending"&&repository.History().Any(x=>x is{Result:"RetryDeferred",Checkpoint:"admission=InputsChanged"}),"真实Worker将准备期间资料变化保存为原命令退避，不能标成功");
                }
            }
            finally{holdCancellation.Cancel();if(File.Exists(marker))File.Delete(marker);try{await holder.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}}
        }
        var mismatch=Package(Path.Combine(root,"mismatch"),true);
        await using(var catalog=new BackgroundPluginCatalog())
        {await catalog.LoadAsync(mismatch.Root,mismatch.Trust,default);Check(catalog.Statuses.Single().State=="Failed","受信包中的公共契约字节不同于宿主时拒绝加载，不静默绑定另一版本");}
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticRealRunnerAdmission",WholeGoalComplete=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS 准入错误边界合计{passed}；包含真实子进程、真实准入等待及自然30秒持久退避，不操作正式服务。");
    }
    private static (string Root,string Trust) Package(string directory,bool mismatch=false)
    {
        var package=WorkerRemediationChecks.Package(directory,("crs.admission","AdmissionFailureEntry"));
        var rows=JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(package.Trust))!;var row=rows.Single();
        var path=Path.Combine(package.Root,row.Directory,"CRS.Common.dll");File.Copy(typeof(WorkAdmissionException).Assembly.Location,path);
        if(mismatch)File.AppendAllText(path,"synthetic-different-contract-bytes");
        row.Files["CRS.Common.dll"]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        File.WriteAllText(package.Trust,JsonSerializer.Serialize(rows));return package;
    }
}
