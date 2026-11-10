using System.Diagnostics;
using System.Text.Json.Nodes;
using CRS.Maintenance;
using CRS.Worker.Service;

internal static class CleanupTaskChecks
{
    private static int passed;
    private static readonly AutomaticCleanupSchedule Fast=AutomaticCleanupSchedule.Default with{DutyCycle=1};
    private static void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS 后台清理任务："+text);}
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.CleanupTasks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await SubmitAndPersistenceAsync(root);await DeduplicateAndPauseAsync(root);await CrashRecoveryAsync(root);await QueuedRecoveryAsync(root);await OrphanedSubmissionRetryAsync(root);await CompletionHandoffAsync(root);await CorruptStoreAsync(root);
        Console.WriteLine($"后台清理任务隔离验证通过：{passed}项。隔离目录：{root}");
    }
    private static async Task<DataCleanupTask> FinishedAsync(WorkerDataCleanup cleanup,string sid)
    {
        var deadline=Stopwatch.StartNew();
        while(true)
        {
            var overview=cleanup.TaskOverview(sid);
            if(overview.Current is null&&overview.LatestResult is{} task&&overview.CanSubmit)return task;
            if(deadline.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("合成清理任务未结束。");
            await Task.Delay(10);
        }
    }
    private static async Task SubmitAndPersistenceAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);var id=Guid.NewGuid();var key=File.ReadAllBytes(Path.Combine(f.Root,"service-key.dpapi"));
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var timer=Stopwatch.StartNew();var submitted=cleanup.SubmitTask(id,"admin-a",Fast);
            Check(timer.Elapsed<TimeSpan.FromSeconds(2)&&submitted.Current?.Id==id,"提交只等待任务落盘，不等待扫描或删除");
            var result=await FinishedAsync(cleanup,"admin-a");
            Check(result.State=="Completed"&&result.DeletedFiles==17&&result.CountsComplete,"前台不参与等待，后台独立完成扫描及17个合成文件清理");
            Check(cleanup.TaskOverview("admin-a").Notification?.Id==id,"后台完成后提供未读结果通知");
            Check(cleanup.TaskOverview("admin-b").LatestResult is null,"其他管理员不能读取个人提交记录");
            Check(cleanup.SubmitTask(id,"admin-a",Fast).LatestResult?.Id==id,"相同提交编号重试不再次执行");
            Check(key.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root,"service-key.dpapi")))&&File.ReadAllText(Path.Combine(f.Root,"plugins/crs.national-team/raw/evidence.pdf"))=="original evidence","当前密钥和插件原件原字节保留");
            Check(Directory.Exists(Path.Combine(f.Root,"maintenance",f.Current))&&f.Old.All(x=>!Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"仅保留最新有效回退副本");
        }
        await using(var restart=new WorkerDataCleanup(f.Root,f.Clock))
        {
            Check(restart.TaskOverview("admin-a").Notification?.Id==id,"后台重启仍保存结果，前台不需要原操作编号");
            Check(restart.AcknowledgeTask(id,"admin-a").Notification is null,"确认通知后不再重复提醒");
            Check(restart.TaskHistory("admin-a").Single().Checkpoint!.Contains("17 个文件")&&restart.TaskHistory("admin-b").Count==0,
                "历史跨重启及通知确认保留，按提交人隔离");
        }
        await using var again=new WorkerDataCleanup(f.Root,f.Clock);
        Check(again.TaskOverview("admin-a").Notification is null,"通知已读状态跨后台重启保留");
    }
    private static void AddSlowFiles(DataCleanupChecks.Fixture f)
    {
        var path=Path.Combine(f.Root,"maintenance",f.Old[2],"snapshot/unknown");Directory.CreateDirectory(path);
        for(var i=0;i<300;i++)File.WriteAllText(Path.Combine(path,i+".raw"),"unknown preserve");
    }
    private static async Task DeduplicateAndPauseAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);AddSlowFiles(f);
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        var first=cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast with{DutyCycle=.01});
        Check(cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast).Current?.Id==first.Current?.Id,"连续点击返回同一在途任务，不重复排队");
        try{cleanup.SubmitTask(Guid.NewGuid(),"admin-b",Fast);throw new Exception("跨身份冒领任务。");}catch(InvalidOperationException){Check(true,"其他管理员不能借提交接口冒领在途任务");}
        await cleanup.PauseAutomaticAsync(default);var finished=await FinishedAsync(cleanup,"admin-a");
        Check(finished.State=="Interrupted"&&finished.DeletedFiles==0,"升级可暂停后台托管任务，不因清理数小时阻塞维护");
        using var lease=new FileStream(Path.Combine(f.Root,"maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        Check(cleanup.TaskOverview("admin-a").Notification?.State=="Interrupted","暂停结果如实通知未完成");
    }
    private static async Task CrashRecoveryAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);Guid id;
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock)){id=cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast).Current!.Id;await FinishedAsync(cleanup,"admin-a");}
        var path=Path.Combine(f.Root,"maintenance/cleanup-tasks",id.ToString("N")+".json");var record=JsonNode.Parse(File.ReadAllText(path))!;
        record["Task"]!["State"]="Running";record["Task"]!["FinishedAt"]=null;record["Task"]!["DeletedFiles"]=7;File.WriteAllText(path,record.ToJsonString());
        var before=File.ReadAllBytes(path);
        await using(var audit=new WorkerDataCleanup(f.Root,f.Clock)){_=audit.Audit(new());}
        Check(before.SequenceEqual(File.ReadAllBytes(path)),"只读审计不会把仍在运行的任务改写成中断");
        await using var restart=new WorkerDataCleanup(f.Root,f.Clock);restart.InitializeTasks();var result=restart.TaskOverview("admin-a").Notification;
        Check(result is{State:"Interrupted",CountsComplete:false,DeletedFiles:7},"异常退出后保留已知计数，并标记数量未完整记录，不能报告完成或删除零个");
    }
    private static async Task QueuedRecoveryAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);Guid id;
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock)){id=cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast).Current!.Id;await FinishedAsync(cleanup,"admin-a");}
        var path=Path.Combine(f.Root,"maintenance/cleanup-tasks",id.ToString("N")+".json");var record=JsonNode.Parse(File.ReadAllText(path))!;
        record["Task"]!["State"]="Queued";record["Task"]!["FinishedAt"]=null;record["Task"]!["DeletedFiles"]=0;record["Task"]!["DeletedBytes"]=0;File.WriteAllText(path,record.ToJsonString());
        await using var restart=new WorkerDataCleanup(f.Root,f.Clock);
        restart.InitializeAutomaticSchedule(Fast);
        await restart.RunAutomaticOnceAsync(null,default,recoverOnly:true);
        Check((await FinishedAsync(restart,"admin-a")).Id==id,"尚未开始的已提交任务在服务重启后继续执行，任务编号不变");
    }
    private static async Task CorruptStoreAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);var path=Path.Combine(f.Root,"maintenance/cleanup-tasks");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"index.json"),"{broken");
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        try{cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast);throw new Exception("损坏索引允许删除。");}
        catch(System.Text.Json.JsonException){Check(f.Old.All(x=>Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"损坏任务索引阻止清理，不阻止业务服务实例创建且不删除副本");}
    }
    private static async Task OrphanedSubmissionRetryAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);Guid id;
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock)){id=cleanup.SubmitTask(Guid.NewGuid(),"admin-a",Fast).Current!.Id;await FinishedAsync(cleanup,"admin-a");}
        var path=Path.Combine(f.Root,"maintenance/cleanup-tasks",id.ToString("N")+".json");var record=JsonNode.Parse(File.ReadAllText(path))!;
        record["Task"]!["State"]="Queued";record["Task"]!["FinishedAt"]=null;File.WriteAllText(path,record.ToJsonString());
        File.Delete(Path.Combine(f.Root,"maintenance/cleanup-tasks/index.json"));
        await using var restart=new WorkerDataCleanup(f.Root,f.Clock);var retry=restart.SubmitTask(id,"admin-a",Fast);
        Check(retry.Current?.Id==id&&(await FinishedAsync(restart,"admin-a")).Id==id,"受理落盘中途退出后，原提交编号重试补齐索引并执行，不丢任务");
    }
    private static async Task CompletionHandoffAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        for(var attempt=0;attempt<12;attempt++)
        {
            var id=Guid.NewGuid();cleanup.SubmitTask(id,"admin-a",Fast);
            while(true)
            {
                var overview=cleanup.TaskOverview("admin-a");
                if(overview.Current?.Id!=id&&overview.LatestResult?.Id!=id)throw new Exception("结果保存期间任务从摘要中消失。");
                if(overview.Current is null&&overview.LatestResult?.Id==id&&overview.CanSubmit)break;
                await Task.Delay(1);
            }
        }
        Check(true,"12轮快速完成交接中始终可见当前任务或已保存结果，不出现空档");
    }
}
