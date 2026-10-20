using System.Diagnostics;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

internal static class SourceBudgetProcessChecks
{
    internal static async Task RunAsync(string package)
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.SourceBudgetProcess",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var fixtures=WorkerRemediationChecks.Package(root,("crs.source-a","SourceBudgetEntry"),("crs.source-b","SourceBudgetEntry"));
        var data=Path.Combine(root,"service");var pipe="CRS.SourceBudgetProcess."+Guid.NewGuid().ToString("N");
        var client=new BackgroundPipeClient(pipe);var passed=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS 来源进程恢复："+name);}
        Process Launch()
        {
            var info=new ProcessStartInfo(Path.Combine(Path.GetFullPath(package),"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{"--console","--data",data,"--plugins",fixtures.Root,"--trust",fixtures.Trust,"--pipe",pipe})info.ArgumentList.Add(arg);
            var child=Process.Start(info)!;child.OutputDataReceived+=(_,_)=>{};child.ErrorDataReceived+=(_,_)=>{};child.BeginOutputReadLine();child.BeginErrorReadLine();return child;
        }
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(true)
            {
                var reply=await client.SendAsync(request,deadline.Token);
                if(reply.Code!="RateLimited")return reply;
                await Task.Delay(250,deadline.Token);
            }
        }
        Task<BackgroundResponse> Health()=>Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));
        async Task<ExecutionRecord> Run(string plugin,string job)
        {
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",plugin,job,Payload:"{}");
            var accepted=await Send(request);Check(accepted.Code=="Accepted","本次持久命令已受理："+plugin+"/"+job+" code="+accepted.Code);
            var until=DateTimeOffset.UtcNow.AddSeconds(20);
            while(DateTimeOffset.UtcNow<until)
            {
                var state=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
                if(state.Receipt is {State:"Completed",ExecutionId:{} id})
                {
                    var execution=(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:id))).Execution;
                    Check(execution is not null&&execution.PluginId==plugin&&execution.JobId==job,"按本次RequestId/ExecutionId核验最终执行");
                    return execution!;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("Source command did not complete.");
        }
        DateTimeOffset blocked;byte[] keyBefore;
        using(var first=Launch())
        {
            try
            {
                Check((await Health()).Health?.State=="Running","独立正式Worker启动并加载两个插件");
                var rejected=await Run("crs.source-a","reject-sse");
                Check(rejected is {Result:"Succeeded",Checkpoint:"simulated=429;network=0"},"插件A实际提交共享429冷却，无网络调用");
                var before=(await Health()).Health!.Sources!.Single(x=>x.Source=="SSE");blocked=before.BlockedUtc;
                Check(blocked>DateTimeOffset.UtcNow.AddHours(47),"真实管道健康返回两天Retry-After");
                Check((await Run("crs.source-b","request-sse")) is {Result:"Rejected",Checkpoint:"source=SourceCooldown;network=0"},"插件B被同一来源共享额度阻断");
                Check((await Run("crs.source-b","request-szse")) is {Result:"Succeeded",Checkpoint:"lease=Granted;network=0"},"另一交易所仍可独立取得额度");
                keyBefore=File.ReadAllBytes(Path.Combine(data,"service-key.dpapi"));
            }
            finally{if(!first.HasExited)first.Kill();await first.WaitForExitAsync();}
        }
        using(var second=Launch())
        {
            try
            {
                var health=(await Health()).Health;
                Check(health?.State=="Running"&&health.Sources?.Single(x=>x.Source=="SSE").BlockedUtc==blocked,"真实强杀/重启后冷却期限完全保留");
                Check(keyBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(data,"service-key.dpapi"))),"重启未重新生成服务密钥");
                Check((await Run("crs.source-b","request-sse")) is {Result:"Rejected",Checkpoint:"source=SourceCooldown;network=0"},"重启后另一个插件仍不能绕过429");
                Check((await Run("crs.source-a","request-szse")) is {Result:"Succeeded",Checkpoint:"lease=Granted;network=0"},"重启后健康来源无遗留占用");
            }
            finally{if(!second.HasExited)second.Kill();await second.WaitForExitAsync();}
        }
        Console.WriteLine($"来源进程恢复通过：{passed}项。隔离现场：{root}");
    }
}
