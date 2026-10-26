using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Worker.Ipc;

/// <summary>只取消本客户端的真实只读查询，观察已存在作业；不排队、不改计划、不注入日志故障。</summary>
internal static class EtfQueryCancelLive
{
    public static async Task Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("取消取证目录已存在，拒绝覆盖原结果。");
        Directory.CreateDirectory(output);
        var pipe=new BackgroundPipeClient();var json=new JsonSerializerOptions{WriteIndented=true};
        async Task<BackgroundResponse> Read(string command,string? execution=null)
        {
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),command,ExecutionId:execution),limit.Token);
        }
        async Task Save(string name,object value)=>await File.WriteAllTextAsync(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,json));
        var started=CRS.Timing.BeijingClock.Now;var watch=Stopwatch.StartNew();BackgroundResponse? before=null,activity=null;
        while(watch.Elapsed<TimeSpan.FromMinutes(6))
        {
            before=await Read("GetHealth");activity=await Read("GetTaskActivity");
            if(before.Health?.State=="Running"&&activity.TaskActivity?.Rows.Count(x=>x.PluginId=="crs.national-team"&&x.State=="Running"&&x.ExecutionId is not null)>=2)break;
            await Task.Delay(2000);
        }
        var original=activity?.TaskActivity?.Rows.Where(x=>x.PluginId=="crs.national-team"&&x.State=="Running"&&x.ExecutionId is not null).ToArray()??[];
        await Save("before",new{StartedAt=started,ObservedAt=CRS.Timing.BeijingClock.Now,Health=before,Activity=activity});
        if(original.Length<2){Console.WriteLine("Unconfirmed：未观察到两项自然在途作业，没有启动新任务。");Environment.ExitCode=2;return;}

        // 客户端断开触发服务端原有只读取消；只记录取消事实，不将未返回的金融数据视作空列表。
        using var cancel=new CancellationTokenSource();var reader=new WorkerEtfWorkspace(pipe);watch.Restart();
        var query=reader.DashboardAsync(null,cancel.Token);await Task.Delay(500);
        var inFlight=!query.IsCompleted;cancel.Cancel();string outcome;
        try{await query;outcome="CompletedBeforeCancellation";}
        catch(OperationCanceledException){outcome="ClientCanceled";}
        catch(Exception error){outcome=error.GetType().Name;}
        await Save("cancellation",new{CheckedAt=CRS.Timing.BeijingClock.Now,InFlightAtCancel=inFlight,Outcome=outcome,ElapsedMs=watch.ElapsedMilliseconds});

        // 始终回查原执行ID；两分钟只是观察期限，仍在途必须保留未确认，不能冒充成功或重新触发。
        var final=new Dictionary<string,BackgroundResponse>();watch.Restart();
        while(watch.Elapsed<TimeSpan.FromMinutes(2))
        {
            foreach(var row in original)final[row.ExecutionId!]=await Read("GetExecution",row.ExecutionId);
            await Save("executions",final);
            if(final.Values.All(x=>x.Execution?.EndedUtc is not null))break;
            await Task.Delay(2000);
        }
        var after=await Read("GetHealth");var afterActivity=await Read("GetTaskActivity");
        var healthy=before?.Health?.State=="Running"&&after.Health?.State=="Running"&&before.Health.ProcessId==after.Health.ProcessId;
        var completed=final.Count==original.Length&&final.Values.All(x=>x.Code=="Ok"&&x.Execution is{EndedUtc:not null,Result:"Succeeded"});
        var success=inFlight&&outcome=="ClientCanceled"&&healthy&&completed;
        var result=new{Status=success?"Observed":"Unconfirmed",ReadOnly=true,CheckedAt=CRS.Timing.BeijingClock.Now,InFlightAtCancel=inFlight,Outcome=outcome,SameHealthyServicePid=healthy,
            OriginalExecutionsSucceeded=completed,OriginalExecutionIds=original.Select(x=>x.ExecutionId).ToArray(),Health=after,Activity=afterActivity,
            Boundary="客户端取消及原作业结果；插件PID另由独立进程快照核验，不推断所有未来取消或模型有效性"};
        await Save("summary",result);Console.WriteLine(JsonSerializer.Serialize(result));if(!success)Environment.ExitCode=2;
    }
}
