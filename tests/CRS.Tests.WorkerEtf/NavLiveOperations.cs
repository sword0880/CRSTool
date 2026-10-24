using System.IO;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>只对固定159915提交一次持久命令；请求先落盘，之后复用原ID，不以重发掩盖未知执行结果。</summary>
internal static class NavLiveOperations
{
    public static async Task Run(string output,bool submit)
    {
        Directory.CreateDirectory(output);var path=Path.Combine(output,"request.json");var json=new JsonSerializerOptions{WriteIndented=true};
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=deadline.Token;var pipe=new BackgroundPipeClient();
        BackgroundRequest request;
        if(File.Exists(path))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(path,token))??throw new InvalidDataException("NAV请求损坏。");
        else
        {
            if(!submit)throw new InvalidDataException("没有稳定NAV请求，不能查询猜测的ID。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","nav-sync",Payload:JsonSerializer.Serialize(new EtfRequest("nav-sync","SZSE","159915")));
            // 同目录并发启动时只允许一个请求身份落盘；冲突或中途写坏须停止核查，不能覆盖后重新排队。
            await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await JsonSerializer.SerializeAsync(file,request,json,token);
            await file.FlushAsync(token);
        }
        if(submit)
        {
            var accepted=await pipe.SendAsync(request,token);await File.WriteAllTextAsync(Path.Combine(output,"submitted.json"),JsonSerializer.Serialize(accepted,json),token);
            Console.WriteLine("QUEUE NAV："+accepted.Code+"；受理不代表来源或事实已验收。");
        }
        BackgroundResponse command;
        while(true)
        {
            command=await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(command.Code=="RateLimited"){await Task.Delay(500,token);continue;}
            if(command.Receipt is null)throw new InvalidDataException("没有NAV持久回执，保留请求文件等待核查。");
            if(!submit||command.Receipt.State=="Completed"||command.Receipt.Code=="RetryDeferred")break;
            await Task.Delay(1000,token);
        }
        var execution=command.Receipt?.ExecutionId is {} id?await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:id),token):null;
        await File.WriteAllTextAsync(Path.Combine(output,"command-status.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Command=command,Execution=execution},json),token);
        Console.WriteLine($"READ NAV：{command.Receipt?.State}/{command.Receipt?.Code}；原请求{request.RequestId}。");
        if(command.Receipt is not {State:"Completed",Code:"Succeeded"}){Environment.ExitCode=2;return;}
        var reader=new WorkerEtfWorkspace(pipe);var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
        var rows=await reader.NavPointsAsync(new("SZSE","159915"),today.AddDays(-365),today,token);
        if(rows.Count==0)throw new InvalidDataException("成功命令未回读到NAV事实，不能通过验收。");
        await File.WriteAllTextAsync(Path.Combine(output,"nav-points.json"),JsonSerializer.Serialize(rows,json),token);
        Console.WriteLine($"READ 正式NAV {rows.Count}条；最新{rows[^1].Date:yyyy-MM-dd}，单位净值{rows[^1].UnitNavYuan}，首次登记{rows[^1].FirstSeenAt:O}。");
    }
}
