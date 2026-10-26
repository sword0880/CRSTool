using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Worker.Ipc;

/// <summary>正式只读阶段采样；失败也保存前后健康，不把单次诊断成功视为性能验收。</summary>
internal static class DashboardProfileLive
{
    public static async Task Run(string output)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("采样目录已存在，拒绝覆盖。");Directory.CreateDirectory(output);
        var pipe=new BackgroundPipeClient();BackgroundResponse? before=null,after=null,reply=null;JsonElement? profile=null;Exception? failure=null;
        async Task<BackgroundResponse> Health(){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));return await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),limit.Token);}
        var timer=Stopwatch.StartNew();
        try
        {
            before=await Health();timer.Restart();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            reply=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:JsonSerializer.Serialize(new EtfRequest("dashboard-profile"))),stop.Token);
            if(reply.Code!="Ok"||reply.Payload is null)throw new InvalidDataException("诊断查询不可用："+reply.Code);
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(reply.Payload);using var parsed=JsonDocument.Parse(page.Items);
            if(page.NextOffset is not null||page.TotalRows!=1||parsed.RootElement.GetArrayLength()!=1)throw new InvalidDataException("诊断必须为单条完整回执。");
            profile=parsed.RootElement[0].Clone();
        }
        catch(Exception error){failure=error;}
        var elapsed=timer.ElapsedMilliseconds;
        try{after=await Health();}catch(Exception error){failure??=error;}
        var healthy=before?.Health?.State=="Running"&&after?.Health?.State=="Running"&&before.Health.ProcessId==after.Health.ProcessId;
        var result=new{Status=failure is null&&healthy?"Succeeded":"Unavailable",CheckedAt=CRS.Timing.BeijingClock.Now,ElapsedMs=elapsed,SameHealthyPid=healthy,Before=before?.Health,After=after?.Health,
            Profile=profile,Code=reply?.Code,Error=failure?.GetType().Name,Message=failure?.Message,Boundary="原件阶段含准入和读取；投影含SQL；IPC残差不可全称管道耗时；缓存未受控"};
        await File.WriteAllTextAsync(Path.Combine(output,"profile.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(result));if(failure is not null||!healthy)Environment.ExitCode=2;
    }
}
