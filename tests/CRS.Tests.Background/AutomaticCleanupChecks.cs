using System.Diagnostics;
using System.Text.Json;
using CRS.Worker.Service;

internal static class AutomaticCleanupChecks
{
    private static int passed;
    private static readonly AutomaticCleanupSchedule Fast=AutomaticCleanupSchedule.Default with{StartupDelay=TimeSpan.Zero,CheckInterval=TimeSpan.FromMilliseconds(10),DutyCycle=1};
    private static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS 自动清理："+name);}
    private static Task<CRS.Maintenance.DataCleanupStatus> Start(WorkerDataCleanup cleanup,AutomaticCleanupSchedule? schedule=null)
        =>cleanup.TryStartAutomatic(default,schedule??Fast)??throw new Exception("未受理自动清理。");
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.AutomaticCleanup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await RetentionAndRestartAsync(root);
        await DamagedHistoryAsync(root);
        await ManualAndMaintenanceAsync(root);
        await PauseAndCorruptionAsync(root);
        await SchedulerAsync(root);
        await PlanOverviewAsync(root);
        Console.WriteLine($"自动清理隔离验证通过：{passed}项。隔离目录：{root}");
    }
    private static async Task PlanOverviewAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        Check(cleanup.TaskOverview("admin").AutomaticPlan is{State:"NotStarted",NextDueAt:null},"尚未启动循环时不报告虚假的下一次执行");
        using var stop=new CancellationTokenSource();
        var schedule=AutomaticCleanupSchedule.Default with{StartupDelay=TimeSpan.FromHours(2)};
        var loop=cleanup.RunAutomaticAsync((_,_,_)=>{},stop.Token,schedule);
        try
        {
            var plan=cleanup.TaskOverview("admin").AutomaticPlan!;
            Check(plan is{State:"Waiting",StartupDelaySeconds:7200,SuccessIntervalSeconds:86400,RetryIntervalSeconds:3600,LogRetentionDays:14,HistoricalBackupsToKeep:1}
                &&plan.NextDueAt==CRS.Timing.BeijingClock.Read(f.Clock).AddHours(2),"计划摘要来自实际循环参数、启动等待和保留规则");
            await cleanup.PauseAutomaticAsync(default);
            Check(cleanup.TaskOverview("admin").AutomaticPlan?.State=="Paused","维护暂停准确反映到计划摘要");
        }
        finally{stop.Cancel();try{await loop;}catch(OperationCanceledException){}}
    }
    private static async Task RetentionAndRestartAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);var key=File.ReadAllBytes(Path.Combine(f.Root,"service-key.dpapi"));
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var result=await Start(cleanup);
            Check(result.State=="Completed"&&result.DeletedFiles==13,"默认自动清理一个旧日志和三个旧快照");
            Check(Directory.Exists(Path.Combine(f.Root,"maintenance",f.Current))&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[0]))&&f.Old.Skip(1).All(x=>!Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"当前快照和最近一份有效历史快照保留");
            Check(key.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root,"service-key.dpapi")))&&File.ReadAllText(Path.Combine(f.Root,"plugins/crs.national-team/raw/evidence.pdf"))=="original evidence","密钥与正在使用的插件原件保持原字节");
            Check(cleanup.TryStartAutomatic(default,Fast) is null,"完成后不连续重扫，持久化下一次执行时间");
        }
        await using var restart=new WorkerDataCleanup(f.Root,f.Clock);
        Check(restart.TryStartAutomatic(default,Fast) is null,"服务重启不会重复启动当日自动清理");
        f.Clock.Value=f.Clock.Value.AddDays(1).AddMinutes(1);
        Check((await Start(restart)).State=="Completed","到达下一日后可以再次执行，无候选时也正常结束");
    }
    private static async Task DamagedHistoryAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);File.WriteAllText(Path.Combine(f.Root,"maintenance",f.Old[0],"snapshot/service-key.dpapi"),"damaged");
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);var result=await Start(cleanup);
        Check(result.State=="Completed"&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[0]))&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[1])),"损坏副本保留调查，另保留一份真正有效的历史副本");
    }
    private static async Task ManualAndMaintenanceAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var preview=cleanup.BeginPreview(Guid.NewGuid(),"manual-admin",new());
            while(preview.State=="Scanning"){await Task.Delay(10);preview=cleanup.Status(preview.OperationId,"manual-admin");}
            Check(cleanup.TryStartAutomatic(default,Fast) is null,"用户尚在确认手动预览时自动清理不抢占");
            try{await cleanup.PauseAutomaticAsync(default);throw new Exception("维护打断了手动预览");}catch(InvalidOperationException){Check(true,"升级暂停命令保留手动操作，不能把它当作自动任务取消");}
            f.Clock.Value=f.Clock.Value.AddMinutes(16);Check((await Start(cleanup)).State=="Completed","手动预览过期后自动清理恢复");
        }
        f=DataCleanupChecks.Create(root);
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var path=Path.Combine(f.Root,"maintenance/upgrade.json");var record=JsonSerializer.Deserialize<WorkerUpgradeRecord>(File.ReadAllText(path))!;
            File.WriteAllText(path,JsonSerializer.Serialize(record with{Stage="Prepared",NewFactsPossible=false}));
            Check(cleanup.TryStartAutomatic(default,Fast) is null&&f.Old.All(x=>Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"升级未提交时不启动自动清理");
        }
        f=DataCleanupChecks.Create(root);
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            using var lease=new FileStream(Path.Combine(f.Root,"maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var result=await Start(cleanup);
            Check(result.State=="Interrupted"&&result.DeletedFiles==0,"维护锁竞争在任何删除前中止，并稍后重试");
        }
    }
    private static async Task PauseAndCorruptionAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);var extra=Path.Combine(f.Root,"maintenance",f.Old[2],"snapshot/extra");Directory.CreateDirectory(extra);
        for(var i=0;i<300;i++)File.WriteAllText(Path.Combine(extra,i+".raw"),"unknown preserve");
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var run=Start(cleanup,Fast with{DutyCycle=.01});await Task.Delay(50);
            Check(!run.IsCompleted,"暂停验证确实发生在尚未完成的限速自动任务中");
            var timer=Stopwatch.StartNew();await cleanup.PauseAutomaticAsync(default);
            Check((await run).State=="Interrupted"&&timer.Elapsed<TimeSpan.FromSeconds(2),"升级可及时取消限速等待并排空自动任务");
            Check(cleanup.TryStartAutomatic(default,Fast) is null,"暂停后不会立刻重入并抢回维护锁");
            using var lease=new FileStream(Path.Combine(f.Root,"maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            Check(lease.CanWrite&&File.Exists(Path.Combine(extra,"0.raw")),"暂停后维护锁可获取，未核实资料保留");
        }
        f=DataCleanupChecks.Create(root);File.WriteAllText(Path.Combine(f.Root,"maintenance/automatic-cleanup.json"),"invalid json");
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            try{_=cleanup.TryStartAutomatic(default,Fast);throw new Exception("损坏状态被当作首次运行");}catch(JsonException){Check(true,"自动状态损坏时不默认执行删除");}
            await cleanup.PauseAutomaticAsync(default);
            Check(cleanup.TryStartAutomatic(default,Fast) is null&&f.Old.All(x=>Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"状态损坏仍可暂停维护，但不触碰原有副本");
        }
    }
    private static async Task SchedulerAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);using var stop=new CancellationTokenSource();
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
        var loop=cleanup.RunAutomaticAsync((category,_,result)=>{if(category=="AutomaticDataCleanup"){count++;completed.TrySetResult();}},stop.Token,Fast);
        try{await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));await Task.Delay(80);Check(count==1,"真实定时循环按持久状态执行，不在每次检查时重复扫描");}
        finally{stop.Cancel();try{await loop;}catch(OperationCanceledException){}}
    }
}
