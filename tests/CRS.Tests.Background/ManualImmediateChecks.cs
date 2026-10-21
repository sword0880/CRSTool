using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
namespace TaskActivityTests;

internal static class ManualImmediateChecks
{
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS 手动立即执行："+text);}
    private static async Task Until(Func<bool> test)
    {var end=Environment.TickCount64+7000;while(!test()){if(Environment.TickCount64>end)throw new TimeoutException("手动请求没有及时开始。");await Task.Delay(25);}}
    public static async Task RunAsync(string root,byte[] key)
    {
        var package=global::WorkerRemediationChecks.Package(root,("crs.manual","FloodEntry"));
        await using var catalog=new BackgroundPluginCatalog();await catalog.LoadAsync(package.Root,package.Trust,default);
        using var repository=new EncryptedTaskRepository(Path.Combine(root,"manual-immediate.db"),key);
        foreach(var job in catalog.Jobs)repository.RegisterDefault(job.PluginId,job.Descriptor);
        var caller=new BackgroundCaller("manual-test",true);
        BackgroundRequest Request(string job)=>new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.manual",job);
        foreach(var plan in repository.Plans().Where(x=>x.JobId is "one" or "two"))
        {var id=repository.Begin(plan,CRS.Timing.BeijingClock.Now);repository.End(id,plan,new("NotPublished"));}
        var restored=Request("one");repository.AcceptCommand(restored,caller.Sid);
        var deferred=Request("job3");repository.AcceptCommand(deferred,caller.Sid);
        var deferredPlan=repository.Plans().Single(x=>x.JobId=="job3");
        var attempt=repository.Begin(deferredPlan,CRS.Timing.BeijingClock.Now,deferred.RequestId);
        repository.End(attempt,deferredPlan,new("RetryDeferred",RetryAfter:TimeSpan.FromMinutes(10)),deferred.RequestId);
        var deadline=repository.Plans().Single(x=>x.JobId=="job3").NotBefore;
        var hold=Path.Combine(root,"manual-hold.fixture");File.WriteAllText(hold,"synthetic");
        var old=Environment.GetEnvironmentVariable("CRS_WORKER_TEST_HOLD");Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",hold);
        try
        {
            await using var runtime=new BackgroundRuntime(repository,catalog);
            await runtime.StartAsync(default);
            await Until(()=>repository.FindCommand(restored.RequestId,caller.Sid)?.State=="Running");
            Check(true,"重启恢复的首次手动请求不继承此前五分钟定时退避");
            var second=Request("two");Check((await runtime.HandleAsync(second,caller,default)).Code=="Accepted","另一任务立即受理");
            var longExecution=repository.FindCommand(restored.RequestId,caller.Sid)!.ExecutionId;
            var noisePlan=repository.Plans().Single(x=>x.JobId=="job12");
            for(var i=0;i<110;i++)
            {var noise=repository.Begin(noisePlan,CRS.Timing.BeijingClock.Now);repository.End(noise,noisePlan,new("Succeeded"));}
            Check(repository.History().All(x=>x.ExecutionId!=longExecution),"长任务确实已被新历史记录挤出历史窗口");
            BackgroundResponse? first=null;
            var until=Environment.TickCount64+7000;
            do
            {
                first=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",PageSize:1),caller with{CanManage=false},default);
                if(first.TaskActivity is{Running:1,Waiting:1,Pending:1})break;
                var drain=first.NextCursor;
                while(drain is not null){var next=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",Cursor:drain),caller,default);drain=next.NextCursor;}
                if(Environment.TickCount64>until)throw new Exception("排队快照未出现");await Task.Delay(25);
            }while(true);
            var observed=first.TaskActivity!.ObservedAt;var rows=first.TaskActivity.Rows.ToList();
            File.Delete(hold);var cursor=first.NextCursor;
            while(cursor is not null)
            {
                var page=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",Cursor:cursor,PageSize:1),caller,default);
                Check(page.TaskActivity!.ObservedAt==observed,"多页当前任务保持同一采样时刻");
                rows.AddRange(page.TaskActivity.Rows);cursor=page.NextCursor;
            }
            Check(rows.Count==3&&rows.Select(x=>x.RequestId).Distinct().Count()==3,"运行、排队、待执行互不重复，不受历史分页影响");
            Check(rows.Single(x=>x.State=="Running").ExecutionId==longExecution,"历史窗口之外仍显示真正执行中的任务");
            Check(rows.Single(x=>x.State=="Waiting") is{JobId:"two",QueuePosition:1,NotBefore:null},"首次手动请求只等待插件空闲，排队顺序明确");
            Check(rows.Single(x=>x.State=="Pending") is{Reason:"RetryDeferred"} pending&&pending.NotBefore==deadline,"已执行过的延期请求仍显示真实重试期限");
            await Until(()=>repository.FindCommand(second.RequestId,caller.Sid)?.Code=="Succeeded");
            Check(repository.FindCommand(restored.RequestId,caller.Sid)?.Code=="Succeeded","释放前序任务后首次手动请求及时完成");
            Check(repository.FindCommand(deferred.RequestId,caller.Sid) is{State:"Pending",Code:"RetryDeferred"},"延期请求不会因新立即执行逻辑进入忙重试循环");
        }
        finally{File.Delete(hold);Environment.SetEnvironmentVariable("CRS_WORKER_TEST_HOLD",old);}
    }
}
