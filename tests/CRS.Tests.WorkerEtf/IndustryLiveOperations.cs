using System.IO;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>正式验收先保存稳定请求，受理、完成、事实与研究门槛分别留证，不随超时换ID。</summary>
internal static class IndustryLiveOperations
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static async Task<BackgroundResponse> Send(BackgroundPipeClient pipe,BackgroundRequest request,CancellationToken token)
    {while(true){var reply=await pipe.SendAsync(request,token);if(reply.Code!="RateLimited")return reply;await Task.Delay(500,token);}}
    public static async Task RunAsync(string output,bool submit,DateOnly? from=null,DateOnly? through=null)
    {
        Directory.CreateDirectory(output);var requestPath=Path.Combine(output,"request.json");using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(25));var token=limit.Token;var pipe=new BackgroundPipeClient();BackgroundRequest request;
        if(File.Exists(requestPath))request=JsonSerializer.Deserialize<BackgroundRequest>(await File.ReadAllTextAsync(requestPath,token))??throw new InvalidDataException("行业请求文件损坏。");
        else
        {
            if(!submit||from is null||through is null)throw new InvalidDataException("缺少已保存的行业请求身份。");
            request=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","industry-pe-sync",Payload:JsonSerializer.Serialize(new EtfRequest("industry-pe-sync",From:from,To:through)));
            // 并发或失败只允许一个身份落盘；写坏的文件保留供核查，不创建另一个请求掩盖未知结果。
            await using var file=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);await JsonSerializer.SerializeAsync(file,request,Json,token);await file.FlushAsync(token);
        }
        if(submit)
        {
            var payload=EtfBusinessProtocol.ParseRequest(request.Payload??"");if(request.JobId!="industry-pe-sync"||payload.Operation!="industry-pe-sync"||payload.From!=from||payload.To!=through)throw new InvalidDataException("原请求参数不能被替换。");
            var accepted=await Send(pipe,request,token);await File.WriteAllTextAsync(Path.Combine(output,"submitted.json"),JsonSerializer.Serialize(accepted,Json),token);
            Console.WriteLine($"QUEUE 行业请求{request.RequestId}：{accepted.Code}；受理不代表源站或研究验收完成。");
        }
        BackgroundResponse command;
        while(true)
        {
            command=await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),token);
            if(command.Receipt is null)throw new InvalidDataException("没有取得行业持久回执，保留原请求核查。");
            if(!submit||command.Receipt.State=="Completed"||command.Receipt.Code=="RetryDeferred")break;await Task.Delay(1000,token);
        }
        var execution=command.Receipt?.ExecutionId is {} id?await Send(pipe,new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:id),token):null;
        await File.WriteAllTextAsync(Path.Combine(output,"command-status.json"),JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Command=command,Execution=execution},Json),token);
        Console.WriteLine($"READ 行业请求{request.RequestId}：{command.Receipt?.State}/{command.Receipt?.Code}。");
        if(command.Receipt is not {State:"Completed",Code:"Succeeded"})Environment.ExitCode=2;
    }
    public static async Task ReadAsync(DateOnly from,DateOnly through,string output)
    {
        Directory.CreateDirectory(output);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(2));var token=limit.Token;var reader=new WorkerEtfWorkspace();
        var report=await reader.IndustryPeReportAsync(from,through,token);var revisions=await reader.IndustryPeRevisionsAsync(from,through,token);
        await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,Json),token);await File.WriteAllTextAsync(Path.Combine(output,"revisions.json"),JsonSerializer.Serialize(revisions,Json),token);
        Console.WriteLine($"READ 正式行业：{report.Rows.Count}行业，{revisions.Count}修订，日历核验{report.CalendarVerified}，待审{revisions.Count(x=>x.State=="NeedsReview")}；{report.OpportunityState}。");
    }
}
