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
            var deadline=repository.FindExecution(execution)!.Progress!.Last().Detail.Until;
            if(deadline is null||result.RetryAfter<TimeSpan.FromMinutes(119)||
                Math.Abs((repository.Plans().Single().NotBefore!.Value-deadline.Value).TotalSeconds)>2)
                throw new Exception("来源冷却期限未用于持久重试时间，或最终进度丢失Until。");
            Console.WriteLine("PASS 实际后台进度链路：来源冷却写入合法阶段，保留同一延期请求，不再因日志校验抛ArgumentException。");
            var successContext=new Context(Path.Combine(root,"complete-plugin"),key);var counter=new ActivityGateway();
            var completedPayload=JsonSerializer.Serialize(new EtfRequest("market-sync",From:new(2026,10,13),To:new(2026,10,13)));
            await using(var first=new NationalTeamBackgroundSession(successContext,counter))
            {
                await first.StartAsync(default);
                if((await first.ExecuteAsync("market-activity-sync",completedPayload,default)).Status!="Succeeded"||counter.Calls!=2)
                    throw new Exception("初次双市场成交采集未成功。");
            }
            await using(var restored=new NationalTeamBackgroundSession(successContext,counter))
            {
                await restored.StartAsync(default);var reused=await restored.ExecuteAsync("market-activity-sync",completedPayload,default);
                if(reused.Status!="Succeeded"||counter.Calls!=2||reused.Checkpoint?.Contains("existingSources=2")!=true)
                    throw new Exception("全区间已核验后仍请求官网或错误返回NotPublished。");
            }
            Console.WriteLine("PASS 实际后台回执：会话重建后全区间已存在时零新增HTTP，返回Succeeded而非NotPublished。");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private sealed class Context(string path,byte[] key):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>path;public byte[] OpenDataKey()=>key.ToArray();}
    private sealed class CoolingGateway:IPluginHttpGateway
    {public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>Task.FromException<PublicHttpResponse>(new SourceBudgetUnavailableException("SourceCooldown",CRS.Timing.BeijingClock.Now.AddHours(2)));}
    private sealed class ActivityGateway:IPluginHttpGateway
    {
        public int Calls;
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            Calls++;var query=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
            var day=query.GetValueOrDefault("SEARCH_DATE")??query["txtQueryDate"];
            object content=uri.Host=="query.sse.com.cn"
                ?new{sqlId="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",actionErrors=Array.Empty<string>(),result=new[]{new{PRODUCT_CODE="17",TRADE_DATE=day.Replace("-",""),TRADE_AMT="100",TRADE_VOL="200"}}}
                :new[]{new{metadata=new{catalogid="scsj_gprdgk_after",tabkey="tab1",cols=new{gp="深市合计"},conditions=new[]{new{name="txtQueryDate",defaultValue=day}}},
                    data=new[]{new{zbmc="成交金额（亿元）",gp="100"},new{zbmc="成交量（亿）",gp="200"}},error=(string?)null}};
            return Task.FromResult(new PublicHttpResponse(200,JsonSerializer.SerializeToUtf8Bytes(content),"application/json"));
        }
    }
}
