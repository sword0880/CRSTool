using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

/// <summary>同一插件的读分类必须跨越宿主和真实Runner；不依赖仅增加Semaphore名额的源码判断。</summary>
internal static class QueryWorkloadChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        foreach(var isolated in new[]{false,true})await RunMode(root,isolated);
    }
    private static async Task RunMode(string root,bool isolated)
    {
        var checks=0;void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine($"PASS 查询分类({(isolated?"Runner":"进程内")})："+text);}
        var package=WorkerRemediationChecks.Package(root,("crs.workloads","QueryWorkloadEntry"));
        var runner=Path.Combine(AppContext.BaseDirectory,"Runner","CRS.Worker.PluginRunner.exe");
        IsolatedPluginSession? proxy=null;
        await using var catalog=new BackgroundPluginCatalog(processSessions:isolated?p=>proxy=new IsolatedPluginSession(runner,package.Root,package.Trust,p):null);
        await catalog.LoadAsync(package.Root,package.Trust,default);
        Check(catalog.Statuses.Single().State=="Started"&&catalog.QueryRouting("crs.workloads")?.Routes.Length==3,"静态读类别经过受信启动握手和校验");
        var key=RandomNumberGenerator.GetBytes(32);
        using var repository=new EncryptedTaskRepository(Path.Combine(root,Guid.NewGuid().ToString("N")+".db"),key);
        CryptographicOperations.ZeroMemory(key);
        await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
        var caller=new BackgroundCaller("synthetic-query-owner",false);
        BackgroundRequest Request(string operation,string? marker=null,bool block=false)=>new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.workloads",Payload:JsonSerializer.Serialize(new{Operation=operation,Marker=marker,Block=block}));
        async Task Entered(string path)
        {for(var i=0;i<200&&!File.Exists(path);i++)await Task.Delay(20);if(!File.Exists(path))throw new TimeoutException("未进入夹具。");}
        for(var i=0;i<130;i++)
            if((await runtime.HandleAsync(Request("short") with{PluginId="crs.unknown-"+i},caller,default)).Code!="UnknownQuery")throw new Exception("未知插件消耗准入名额。");
        Check(true,"未知插件不创建队列，130个不同无效标识不会挤占受信插件容量");
        using var cancel=new CancellationTokenSource();
        var marker=Path.Combine(root,Guid.NewGuid().ToString("N"));
        var heavy=runtime.HandleAsync(Request("research",marker,true),caller,cancel.Token);await Entered(marker);
        try
        {
            var shortReply=await runtime.HandleAsync(Request("short"),caller,default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(shortReply.Code=="Ok"&&!heavy.IsCompleted,"同一插件重读尚未返回时短读已完成");
            var pendingMarker=Path.Combine(root,Guid.NewGuid().ToString("N"));
            using var queuedCancellation=new CancellationTokenSource();
            var queued=runtime.HandleAsync(Request("research",pendingMarker,true),caller,queuedCancellation.Token);
            var full=await runtime.HandleAsync(Request("research"),caller,default);
            Check(full.Code=="PluginBusy"&&!File.Exists(pendingMarker),"重读最多一个在途和一个等待，满额不调用插件");
            queuedCancellation.Cancel();try{await queued;throw new Exception("排队忽略取消。");}catch(OperationCanceledException){}
            Check(!File.Exists(pendingMarker),"取消排队不会晚到执行，后续短读仍可进入");
            var waiting=await runtime.HandleAsync(Request("research"),caller,default).WaitAsync(TimeSpan.FromSeconds(4));
            Check(waiting.Code=="PluginBusy"&&!heavy.IsCompleted,"重读等待预算用完明确繁忙，原查询继续");
            Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),caller,default)).Health?.State=="Running","等待失败不使Worker退出或误报仓储故障");
        }
        finally{cancel.Cancel();try{await heavy;}catch(OperationCanceledException){}}
        Check((await runtime.HandleAsync(Request("research"),caller,default)).Code=="Ok","取消归还重读名额，后续真实查询成功");
        var detachedMarker=Path.Combine(root,Guid.NewGuid().ToString("N"));
        Check((await runtime.HandleAsync(Request("start-research",detachedMarker),caller,default)).Code=="Ok","脱离提交RPC的研究作业已受理");
        await Entered(detachedMarker);
        using var shortCancel=new CancellationTokenSource();
        var firstMarker=Path.Combine(root,Guid.NewGuid().ToString("N"));var secondMarker=Path.Combine(root,Guid.NewGuid().ToString("N"));
        var first=runtime.HandleAsync(Request("short",firstMarker,true),caller,shortCancel.Token);
        var second=runtime.HandleAsync(Request("short",secondMarker,true),caller,shortCancel.Token);
        await Task.WhenAll(Entered(firstMarker),Entered(secondMarker));
        var saturated=await runtime.HandleAsync(Request("short"),caller,default).WaitAsync(TimeSpan.FromSeconds(3));
        Check(saturated.Code=="PluginBusy"&&!first.IsCompleted&&!second.IsCompleted,"短读同时两项，独立等待预算一秒到期");
        await runtime.EnterMaintenanceAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        foreach(var task in new[]{first,second})try{await task;throw new Exception("维护未取消查询。");}catch(OperationCanceledException){}
        Check((await runtime.HandleAsync(Request("short"),caller,default)).Code=="Maintenance","维护取消并排空全部并发短读后拒绝新查询");
        Check(File.Exists(detachedMarker+".stopped"),"维护在返回前也取消并排空已受理的异步研究作业");
        if(proxy is not null)
        {
            await proxy.StopAsync(default);await catalog.DrainQueriesAsync(default);
            Check(!proxy.CanRun,"Runner已经停止后排空直接结束，不尝试启动新进程");
        }
        Console.WriteLine($"PASS 查询分类 {checks}；隔离模式 {isolated}，不代表正式服务已安装。");
    }
}
