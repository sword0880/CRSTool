using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;

internal static class CoolingProgressChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.CoolingProgress",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            await using var session=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"plugin"),key),new CoolingGateway());
            await session.StartAsync(default);
            using var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler.db"),key);
            var descriptor=session.DescribeJobs().Single(x=>x.JobId=="market-activity-sync");repository.RegisterDefault("crs.national-team",descriptor);
            var plan=repository.Plans().Single();var payload=JsonSerializer.Serialize(new EtfRequest("market-sync",From:new(2026,10,1),To:new(2026,10,1)));
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",plan.PluginId,plan.JobId,Payload:payload);
            repository.AcceptCommand(request,"test");var execution=repository.Begin(plan,CRS.Timing.BeijingClock.Now,request.RequestId);
            var result=await session.ExecuteWithProgressAsync(plan.JobId,payload,progress=>
            {repository.AppendProgress(execution,progress);return Task.CompletedTask;},default,request.RequestId);
            repository.End(execution,plan,result,request.RequestId);
            if(result.Status!="RetryDeferred"||repository.FindExecution(execution)?.Progress?.LastOrDefault()?.Detail.Stage!="Cooldown"||
                repository.FindCommand(request.RequestId,"test") is not {State:"Pending",Code:"RetryDeferred"})
                throw new Exception("来源冷却未保留延期请求或进度未正确持久化。");
            Console.WriteLine("PASS 实际后台进度链路：来源冷却写入合法阶段，保留同一延期请求，不再因日志校验抛ArgumentException。");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private sealed class Context(string path,byte[] key):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>path;public byte[] OpenDataKey()=>key.ToArray();}
    private sealed class CoolingGateway:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>Task.FromException<PublicHttpResponse>(new InvalidOperationException("来源冷却中，合成来源限制"));}
}
