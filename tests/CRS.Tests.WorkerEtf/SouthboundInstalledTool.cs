using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Worker.Ipc;

/// <summary>正式服务只经受限IPC核查；更新命令先保存身份，等待超时后只能沿原ID回查。</summary>
internal static class SouthboundInstalledTool
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public static async Task RunAsync(string action,string directory)
    {
        var root=Path.GetFullPath(directory);Directory.CreateDirectory(root);
        if(action=="legacy-contract"){await LegacyAsync(root);return;}
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(55));var token=deadline.Token;
        var pipe=new BackgroundPipeClient();var reader=new WorkerEtfWorkspace(pipe);
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {var result=await pipe.SendAsync(request,token);await Save(root,request.Command+".json",result,token);return result;}
        var before=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));
        if(before.Code!="Ok"||before.Health is not{State:"Running"})throw new InvalidDataException("后台尚未健康运行，停止取证。");
        if(action=="baseline")
        {
            var jobs=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"));if(jobs.Code!="Ok")throw new InvalidDataException("任务基线不可用。");
            var history=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistory",PageSize:10));if(history.Code!="Ok")throw new InvalidDataException("历史基线不可用。");
            await Save(root,"configuration.json",await reader.ConfigurationAsync(token),token);
            await Save(root,"equity-holdings.json",await reader.EquityHoldingsAsync(null,null,null,false,token),token);
            var year=CRS.Timing.BeijingClock.Now.Year;
            var probe=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:EtfBusinessProtocol.WriteRequest(new("southbound-net-annual",From:new(year-4,1,1),To:new(year,12,31),Count:2))));
            Console.WriteLine($"BASELINE pid={before.Health.ProcessId};jobs={jobs.Jobs?.Count};southbound={probe.Code}");
        }
        else if(action is "collect" or "status")
        {
            var path=Path.Combine(root,"request.json");BackgroundRequest request;
            if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("原请求文件无效。");
            else
            {
                if(action=="status")throw new InvalidDataException("缺少原请求身份，不创建替代请求。");
                request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","southbound-sync",Payload:EtfBusinessProtocol.WriteRequest(new("southbound-net-sync")));
                await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await JsonSerializer.SerializeAsync(file,request,Json,token);
            }
            if(action=="collect")
            {var accepted=await Send(request);if(accepted.Receipt?.RequestId!=request.RequestId)throw new InvalidDataException("更新受理身份未确认，保存原ID后停止。");}
            BackgroundResponse state;
            do
            {
                state=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
                if(state.Receipt?.RequestId!=request.RequestId)throw new InvalidDataException("原更新回执未知，不重新排队。");
                if(state.Receipt.State=="Completed"||state.Receipt.Code=="RetryDeferred"||action=="status")break;
                await Task.Delay(1000,token);
            }while(true);
            if(state.Receipt?.ExecutionId is {} execution)await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:execution));
            Console.WriteLine($"REQUEST id={request.RequestId};state={state.Receipt?.State};code={state.Receipt?.Code}");
            if(state.Receipt is not{State:"Completed",Code:"Succeeded"})Environment.ExitCode=2;
        }
        else if(action=="read")
        {
            var year=CRS.Timing.BeijingClock.Now.Year;var report=await reader.SouthboundAnnualAsync(year-4,year,token);
            await Save(root,"annual-report.json",report,token);
            Console.WriteLine(JsonSerializer.Serialize(new{State="Available",report.Metric,report.CaptureId,report.Through,Years=report.Years.Select(y=>new{y.Year,Valid=y.Points.Count(p=>p.CumulativeNetBuyYiHkd.HasValue),Last=y.Points.LastOrDefault()})}));
            if(report.Years.Any(y=>!y.Points.Any(p=>p.CumulativeNetBuyYiHkd.HasValue)))Environment.ExitCode=2;
        }
        else throw new ArgumentException("未知南向服务核查操作。");
        var after=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),token);await Save(root,"health-after.json",after,token);
        if(after.Health?.State!="Running"||after.Health.ProcessId!=before.Health.ProcessId)throw new InvalidDataException("核查期间后台健康或PID改变。");
    }
    private static Task Save<T>(string directory,string name,T value,CancellationToken token)=>File.WriteAllTextAsync(Path.Combine(directory,name),JsonSerializer.Serialize(value,Json),token);
    private static async Task LegacyAsync(string directory)
    {
        var control=new LegacyControl();var pipeName="CRS.Southbound.Legacy."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var server=new BackgroundPipeServer(control,pipeName).RunAsync(stop.Token);
        try
        {
            try{await new WorkerEtfWorkspace(new(pipeName)).SouthboundAnnualAsync(2022,2026,stop.Token);throw new InvalidDataException("旧后台被错误当成支持新指标。");}
            catch(NotSupportedException error) when(error.Message.Contains("后台版本",StringComparison.Ordinal))
            {
                if(!control.Operations.SequenceEqual(new[]{"query-capabilities","southbound-net-annual"}))throw new InvalidDataException("旧后台检测发起了额外或写操作。");
                await Save(directory,"legacy-contract.json",new{Passed=true,Operations=control.Operations,Message=error.Message},stop.Token);
                Console.WriteLine("PASS 真实IPC旧后台UnknownQuery映射为版本不支持，仅查询且不回退旧曲线。");
            }
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class LegacyControl:IBackgroundControl
    {
        public List<string> Operations {get;}=[];
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command!="QueryPlugin")throw new InvalidDataException("旧后台兼容检查不得提交写命令。");
            Operations.Add(EtfBusinessProtocol.ParseRequest(request.Payload!).Operation);
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownQuery"));
        }
    }
}
