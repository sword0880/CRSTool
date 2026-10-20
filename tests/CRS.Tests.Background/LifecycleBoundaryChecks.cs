using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using CRS.Worker.Ipc;

internal static class LifecycleBoundaryChecks
{
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 生命周期："+label);}
    public static async Task Run(string root)
    {
        foreach(var phase in new[]{"Start","Query","Stop","Dispose"})
        {
            var package=WorkerRemediationChecks.Package(root,("crs.stuck","Hanging"+phase+"Entry"));
            var notified=0;var events=new List<string>();
            var catalog=new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(300),()=>notified++,
                lifecycleDiagnostic:(plugin,stage,code)=>events.Add(plugin+":"+stage+":"+code));
            var watch=Stopwatch.StartNew();
            try
            {
                await catalog.LoadAsync(package.Root,package.Trust,default);
                if(phase=="Query")await catalog.QueryAsync("crs.stuck","ok",default);
                else if(phase!="Start")await catalog.DisposeAsync();
                throw new Exception("未检测失控生命周期。");
            }
            catch(PluginLifecycleTimeoutException error)
            {
                Check(error.PluginId=="crs.stuck"&&error.Phase==phase&&notified==1&&watch.Elapsed<TimeSpan.FromSeconds(5),phase+"失控带明确身份/阶段且有界通知退出");
                Check(events.Contains("crs.stuck:"+phase+":CancellationGraceExceeded"),phase+"只记录分类诊断");
            }
            Check(catalog.RequiresProcessRestart,phase+"终止状态不可冒充健康");
            try{await catalog.DisposeAsync();throw new Exception("失控后仍释放资源。");}catch(PluginLifecycleTimeoutException){Check(true,phase+"失控后禁止再次Stop/Dispose/Unload");}
            try{await catalog.QueryAsync("crs.stuck","ok",default);throw new Exception("失控后继续分派。");}catch(PluginLifecycleTimeoutException){Check(notified==1,phase+"拒绝后续查询且不重复退出回调");}
        }
        var late=WorkerRemediationChecks.Package(root,("crs.late","LateQueryEntry"));
        await using(var catalog=new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(300)))
        {
            await catalog.LoadAsync(late.Root,late.Trust,default);
            try{await catalog.QueryAsync("crs.late","ok",default);throw new Exception("迟到返回被当作成功。");}
            catch(OperationCanceledException){Check(!catalog.RequiresProcessRestart,"协作收尾后拒绝迟到成功，不强制重启");}
        }
        await RuntimeHealth(root);
        var completedTimeout=WorkerRemediationChecks.Package(root,("crs.timeout","TimeoutOnCancelQueryEntry"));
        await using(var catalog=new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(300)))
        {
            await catalog.LoadAsync(completedTimeout.Root,completedTimeout.Trust,default);
            try{await catalog.QueryAsync("crs.timeout","ok",default);throw new Exception("插件异常未传播。");}
            catch(TimeoutException){Check(!catalog.RequiresProcessRestart,"收尾已结束的插件TimeoutException不误判为线程失控");}
        }
        await ProcessExit(root);
        var failed=WorkerRemediationChecks.Package(root,("crs.bad","BadStartEntry"),("crs.good","GoodEntry"));
        await using(var catalog=new BackgroundPluginCatalog(lifecycleDiagnostic:(_,_,_)=>throw new IOException("synthetic diagnostic failure")))
        {
            await catalog.LoadAsync(failed.Root,failed.Trust,default);
            Check(catalog.Statuses.Single(x=>x.PluginId=="crs.good").State=="Started","诊断写入失败不扩大普通初始化故障");
        }
    }
    public static async Task ExitChild(string root,string phase)
    {
        if(phase is not ("Start" or "Query" or "Stop" or "Dispose"))throw new ArgumentException("未知夹具阶段。");
        var package=WorkerRemediationChecks.Package(root,("crs.exit","Hanging"+phase+"Entry"));
        var catalog=new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(300),()=>Environment.Exit(2),
            lifecycleDiagnostic:(plugin,stage,code)=>File.WriteAllText(Path.Combine(root,"exit-event.txt"),plugin+":"+stage+":"+code));
        await catalog.LoadAsync(package.Root,package.Trust,default);
        if(phase=="Query")await catalog.QueryAsync("crs.exit","ok",default);
        else await catalog.DisposeAsync();
        throw new Exception("失控子进程没有按策略退出。");
    }
    private static async Task ProcessExit(string root)
    {
        foreach(var phase in new[]{"Start","Query","Stop","Dispose"})
        {
            var directory=Path.Combine(root,"exit-"+phase);Directory.CreateDirectory(directory);
            var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"CRS.Tests.Background.exe"))
                {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{"--lifecycle-exit-child",directory,phase})info.ArgumentList.Add(argument);
            using var process=Process.Start(info)??throw new Exception("无法创建隔离子进程。");
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(process.ExitCode==2&&File.ReadAllText(Path.Combine(directory,"exit-event.txt"))=="crs.exit:"+phase+":CancellationGraceExceeded",
                    phase+"失控子进程真实退出码2且先留下阶段诊断");
            }
            finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
        }
    }
    private static async Task RuntimeHealth(string root)
    {
        var package=WorkerRemediationChecks.Package(root,("crs.stuck","HangingQueryEntry"));
        var catalog=new BackgroundPluginCatalog(TimeSpan.FromMilliseconds(300),lifecycleDiagnostic:(_,_,_)=>throw new IOException("synthetic diagnostic failure"));
        await catalog.LoadAsync(package.Root,package.Trust,default);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            using var repository=new EncryptedTaskRepository(Path.Combine(root,"terminal-health.db"),key);
            await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
            var caller=new BackgroundCaller(WindowsIdentity.GetCurrent().User!.Value,true);
            try{await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.stuck",Payload:"ok"),caller,default);throw new Exception("失控查询未传播。");}
            catch(PluginLifecycleTimeoutException){}
            var health=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),caller,default);
            Check(health.Health?.State=="PluginUnresponsive","诊断回调失败时失控状态仍对真实控制层可见");
            Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.stuck","one"),caller,default)).Code=="PluginUnresponsive","终止状态拒绝新增写命令");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
}
