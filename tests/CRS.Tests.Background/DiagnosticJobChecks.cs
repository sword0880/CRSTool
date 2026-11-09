using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
internal static class DiagnosticJobChecks
{
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.DiagnosticJobs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            var package=WorkerRemediationChecks.Package(root,("crs.diag","DiagnosticEntry"));
            using var repository=new EncryptedTaskRepository(Path.Combine(root,"jobs.db"),key);
            repository.RegisterDefault("crs.diag",new("one","旧版周期心跳","0/1 * * * * ?","China Standard Time",true));
            await using var catalog=new BackgroundPluginCatalog();await catalog.LoadAsync(package.Root,package.Trust,default);
            await using var runtime=new BackgroundRuntime(repository,catalog);await runtime.StartAsync(default);
            var caller=new BackgroundCaller("test-admin",true);
            Task<BackgroundResponse> Send(string command,long? revision=null)=>runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),command,"crs.diag","one",ExpectedRevision:revision),caller,default);
            void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 诊断任务："+text);}
            Check(repository.Plans().Single() is{Enabled:false,Revision:2},"旧版启用的周期任务转诊断时只禁用一次，保留配置及历史");
            Check((await Send("GetJobs")).Jobs!.Count==0&&(await Send("GetDiagnosticJobs")).Jobs!.Single().JobId=="one","诊断项在独立只读清单，不混入业务计划");
            Check((await Send("GetHistoryJobs")).Jobs!.Single().JobId=="one","执行记录的筛选清单包含诊断任务");
            Check((await Send("Resume",2)).Code=="DiagnosticOnly","不能误将诊断项重新设为周期任务");
            Check((await Send("RunNow")).Code=="Accepted","管理员仍可按需执行调度自检");
            var until=DateTimeOffset.UtcNow.AddSeconds(10);
            while(!repository.History().Any(x=>x.Result=="Succeeded")&&DateTimeOffset.UtcNow<until)await Task.Delay(20);
            Check(repository.History().Single().Result=="Succeeded","实际Quartz执行诊断并记录结果");
            repository.RegisterDefault("crs.diag-extra",new("two","历史任务","0/1 * * * * ?","China Standard Time",false,ManualOnly:true));
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"GetHistoryJobs",PageSize:1);
            var first=await runtime.HandleAsync(request,new("reader",false),default);
            var second=await runtime.HandleAsync(request with{Cursor=first.NextCursor},new("reader",false),default);
            Check(first.Jobs!.Single().JobId=="two"&&first.NextCursor is not null&&second.Jobs!.Single().JobId=="one"&&second.NextCursor is null,
                "只读筛选清单完整分页，插件编号有共同前缀也不漏任务");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
}
