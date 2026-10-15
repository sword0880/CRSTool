using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CRS.Plugin.Abstractions;
using CRS.PluginHost;

/// <summary>以真实Quartz时钟和独立目录验证设置、取消、重启和中断，不触碰用户任务。</summary>
internal static class TaskScheduleChecks
{
    private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
    private static async Task Reject(Func<Task> work,string name)
    {try{await work();}catch{Console.WriteLine("PASS "+name);return;}throw new Exception(name);}
    private static async Task Until(Func<Task<bool>> condition)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!await condition()){await Task.Delay(20,timeout.Token);}}
    public static async Task Run(string root)
    {
        var storage=Path.Combine(root,"plans");var count=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken,Task> behavior=token=>Task.Delay(Timeout.InfiniteTimeSpan,token);
        await using(var clock=new QuartzTaskScheduler(storage))
        {
            await clock.Scope("fixture",CancellationToken.None).RegisterAsync("sync","0 0 0 1 1 ? 2098","China Standard Time",async token=>
            {Interlocked.Increment(ref count);entered.TrySetResult();await behavior(token);},CancellationToken.None,TimeSpan.FromMilliseconds(300),"合成同步");
            await clock.ConfigureSimpleAsync("fixture","sync",ScheduleFrequency.Hourly,0,17,false);
            await Task.Delay(700);Check(count==0,"Paused plan removes Quartz startup trigger");
            var plan=(await clock.ListAsync()).Single();Check(!plan.Plan.Enabled && plan.NextUtc is null && plan.Minute==17,"Saved disabled plan has no next trigger");
            var before=File.ReadAllBytes(Path.Combine(storage,"scheduled-tasks","tasks.json"));
            await Reject(()=>clock.ConfigureAsync("fixture","sync","broken","China Standard Time",true),"Invalid cron rejected before persistence");
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(storage,"scheduled-tasks","tasks.json"))),"Invalid schedule preserves existing file");
            await using(var duplicate=new QuartzTaskScheduler(storage))await Reject(()=>duplicate.ListAsync(),"Second host cannot own the same task store");
            await clock.RunNowAsync("fixture","sync");await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Reject(()=>clock.RunNowAsync("fixture","sync"),"Manual duplicate execution rejected while running");
            await clock.CancelRunAsync("fixture","sync");await Until(async ()=>(await clock.HistoryAsync()).LastOrDefault()?.State==TaskRunState.Cancelled);
            Check(count==1,"Paused plan still permits one explicit manual execution");
            behavior=_=>Task.CompletedTask;await clock.RunNowAsync("fixture","sync");
            await Until(async ()=>(await clock.HistoryAsync()).First().State==TaskRunState.Succeeded);
            Check((await clock.HistoryAsync()).First().Origin=="Manual","Manual success recorded with origin and completion");
            behavior=_=>throw new InvalidOperationException("SECRET-SAMPLE-DO-NOT-PERSIST");await clock.RunNowAsync("fixture","sync");
            await Until(async ()=>(await clock.HistoryAsync()).First().State==TaskRunState.Failed);
            Check(!File.ReadAllText(Path.Combine(storage,"scheduled-tasks","tasks.json")).Contains("SECRET-SAMPLE"),"Execution record stores fault category instead of exception message");
            behavior=_=>throw new PluginTaskIncompleteException();await clock.RunNowAsync("fixture","sync");
            await Until(async ()=>(await clock.HistoryAsync()).First().State==TaskRunState.Incomplete);
            Check((await clock.HistoryAsync()).First().FailureCode=="SOURCE_INCOMPLETE","Partial source failure is not a successful task");
            await clock.Scope("fixture",CancellationToken.None).RegisterAsync("manual-prefixed","0 0 0 1 1 ? 2098","China Standard Time",_=>Task.CompletedTask,CancellationToken.None,TimeSpan.FromMilliseconds(50));
            await Until(async ()=>(await clock.HistoryAsync()).Any(x=>x.TaskId=="manual-prefixed" && x.State==TaskRunState.Succeeded));
            Check((await clock.HistoryAsync()).Single(x=>x.TaskId=="manual-prefixed").Origin=="Startup","Task name cannot change the actual trigger origin");
        }
        // 重建上次进程未结束的记录；恢复必须标为中断，不能再次调用旧回调。
        var file=Path.Combine(storage,"scheduled-tasks","tasks.json");var node=JsonNode.Parse(File.ReadAllText(file))!;
        var unfinished=new TaskRun(Guid.NewGuid().ToString("N"),"fixture","sync",DateTimeOffset.UtcNow,null,TaskRunState.Running,"Scheduled",null);
        node["Runs"]!.AsArray().Add(JsonSerializer.SerializeToNode(unfinished,new JsonSerializerOptions{Converters={new JsonStringEnumConverter()}}));File.WriteAllText(file,node.ToJsonString());
        await using(var restarted=new QuartzTaskScheduler(storage))
        {
            var plan=(await restarted.ListAsync()).Single(x=>x.Plan.TaskId=="sync");Check(!plan.Registered && !plan.Plan.Enabled && plan.Minute==17,"Restart restores plan without loading arbitrary callbacks");
            Check((await restarted.HistoryAsync()).Any(x=>x.Id==unfinished.Id && x.State==TaskRunState.Interrupted),"Unfinished previous execution becomes interrupted");
            await restarted.Scope("fixture",CancellationToken.None).RegisterAsync("sync","0 0 23 * * ?","China Standard Time",_=>{count++;return Task.CompletedTask;},CancellationToken.None,TimeSpan.FromMilliseconds(100));
            await Task.Delay(250);Check(count==4 && !(await restarted.ListAsync()).Single(x=>x.Plan.TaskId=="sync").Plan.Enabled,"Plugin defaults do not overwrite saved pause on restart");
        }
        var corrupt=Path.Combine(root,"corrupt-plans","scheduled-tasks");Directory.CreateDirectory(corrupt);var corruptFile=Path.Combine(corrupt,"tasks.json");File.WriteAllText(corruptFile,"{broken");
        await using(var invalid=new QuartzTaskScheduler(Path.GetDirectoryName(corrupt)))await Reject(()=>invalid.ListAsync(),"Corrupt persisted plans fail closed");
        Check(File.ReadAllText(corruptFile)=="{broken","Corrupt task file is not reset or overwritten");
        var retention=Path.Combine(root,"retention");
        using(var store=new TaskPlanStore(retention))
        {
            store.AddDefault(new("fixture","sync","合成任务","0 0 23 * * ?","China Standard Time",true));store.AddRun(unfinished);
            for(var i=0;i<505;i++)store.AddRun(new(Guid.NewGuid().ToString("N"),"fixture","sync",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,TaskRunState.Succeeded,"Manual",null));
            Check(store.Runs().Count==500 && store.Runs().Any(x=>x.Id==unfinished.Id),"Bounded history retains unfinished long tasks");
        }
    }
}
