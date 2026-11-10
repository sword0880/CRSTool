using System.Diagnostics;
using System.Text.Json;
using CRS.Worker.Service;

internal static class AutomaticCleanupChecks
{
    private static int passed;
    private static readonly AutomaticCleanupSchedule Fast=AutomaticCleanupSchedule.Default with{DutyCycle=1};
    private static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS 自动清理："+name);}
    private static Task<CRS.Maintenance.DataCleanupStatus> Start(WorkerDataCleanup cleanup,AutomaticCleanupSchedule? schedule=null)
        =>cleanup.TryStartAutomatic(default,schedule??Fast)??throw new Exception("未受理自动清理。");
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.AutomaticCleanup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await RetentionAndRestartAsync(root);
        await DamagedHistoryAsync(root);
        await MissingArchiveAsync(root);
        await ManualAndMaintenanceAsync(root);
        await PauseAndCorruptionAsync(root);
        await SchedulerAsync(root);
        await PlanOverviewAsync(root);
        await RetryLimitAsync(root);
        Console.WriteLine($"自动清理隔离验证通过：{passed}项。隔离目录：{root}");
    }

    private static async Task RetryLimitAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        var notices=0;
        cleanup.InitializeAutomaticSchedule(Fast,_=>notices++);
        using var lease=new FileStream(Path.Combine(f.Root,"maintenance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        for(var attempt=1;attempt<=4;attempt++)
        {
            Check((await Start(cleanup)).State=="Interrupted","维护锁竞争触发安全重试");
            var plan=cleanup.TaskOverview("admin").AutomaticPlan!;
            if(attempt<4)
            {
                Check(notices==0&&plan.Failures==attempt&&plan.NextDueAt==f.Clock.Value+Fast.RetryInterval,"清理重试前保留失败次数和期限");
                f.Clock.Value=plan.NextDueAt!.Value;
            }
            else Check(notices==1&&plan.Failures==0&&plan.NextDueAt==Fast.NextAfter(f.Clock.Value),"清理三次重试用尽后通知并恢复每日计划");
        }
    }
    private static async Task PlanOverviewAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        Check(cleanup.TaskOverview("admin").AutomaticPlan is{State:"NotStarted",NextDueAt:null},"尚未启动循环时不报告虚假的下一次执行");
        cleanup.InitializeAutomaticSchedule(AutomaticCleanupSchedule.Default);
        var plan=cleanup.TaskOverview("admin").AutomaticPlan!;
        Check(plan is{State:"Waiting",Cron:"0 0 3 * * ?",TimeZoneId:"China Standard Time",RetryIntervalSeconds:3600,LogRetentionDays:14,KeepLatestUsableBackupOnly:true}
            &&plan.NextDueAt==new DateTimeOffset(2026,10,28,3,0,0,TimeSpan.FromHours(8)),"计划显示北京时间每日 03:00 的 Cron 和下次执行");
        File.WriteAllText(Path.Combine(f.Root,"maintenance/automatic-cleanup.json"),JsonSerializer.Serialize(new
        {
            Format=1,NextRunAt=f.Clock.Value.AddDays(1),LastOperationId=(Guid?)null,LastState="Completed"
        }));
        Check(cleanup.TaskOverview("admin").AutomaticPlan?.NextDueAt==plan.NextDueAt,"旧版按间隔保存的记录可接续到新的 Cron 时间");
        await cleanup.PauseAutomaticAsync(default);
        Check(cleanup.TaskOverview("admin").AutomaticPlan?.State=="Paused","维护暂停准确反映到计划摘要");
    }
    private static async Task RetentionAndRestartAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);var key=File.ReadAllBytes(Path.Combine(f.Root,"service-key.dpapi"));
        await using(var cleanup=new WorkerDataCleanup(f.Root,f.Clock))
        {
            var result=await Start(cleanup);
            Check(result.State=="Completed"&&result.DeletedFiles==17,"默认自动清理一个旧日志和四个旧快照");
            Check(Directory.Exists(Path.Combine(f.Root,"maintenance",f.Current))&&f.Old.All(x=>!Directory.Exists(Path.Combine(f.Root,"maintenance",x))),"当前快照可用时只保留当前快照");
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
        var f=DataCleanupChecks.Create(root);
        File.WriteAllText(Path.Combine(f.Root,"maintenance",f.Current,"snapshot/service-key.dpapi"),"damaged");
        File.WriteAllText(Path.Combine(f.Root,"maintenance",f.Old[0],"snapshot/service-key.dpapi"),"damaged");
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);var result=await Start(cleanup);
        Check(result.State=="Completed"&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[0]))&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[1]))
            &&result.Message.Contains("2 份备份未能核实"),"当前快照不可用时保留一份真正有效的历史副本，并说明保留的异常副本");
    }
    private static async Task MissingArchiveAsync(string root)
    {
        var f=DataCleanupChecks.Create(root);
        File.Delete(Path.Combine(f.Root,"maintenance",f.Old[3]+".json"));
        await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        Check((await Start(cleanup)).DeletedFiles==13&&Directory.Exists(Path.Combine(f.Root,"maintenance",f.Old[3])),
            "无归档的副本保留，其余已核实旧副本仍清理");
        f.Clock.Value=f.Clock.Value.AddDays(1).AddMinutes(1);
        var next=await Start(cleanup);
        Check(next.DeletedFiles==0&&next.Message.Contains("1 份备份未能核实"),"零删除通知说明未能核实的副本");
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
        var f=DataCleanupChecks.Create(root);await using var cleanup=new WorkerDataCleanup(f.Root,f.Clock);
        cleanup.InitializeAutomaticSchedule(Fast);
        var count=0;
        void Observe(string category,string job,string result){if(category=="AutomaticDataCleanup")count++;}
        await cleanup.RunAutomaticOnceAsync(Observe,default,recoverOnly:true);
        Check(count==0,"新服务的协调循环等待首次 Cron 触发");
        f.Clock.Value=Fast.NextAfter(f.Clock.Value);
        await cleanup.RunAutomaticOnceAsync(Observe,default,recoverOnly:false);
        await cleanup.RunAutomaticOnceAsync(Observe,default,recoverOnly:true);
        Check(count==1&&cleanup.TaskOverview("admin").AutomaticPlan?.NextDueAt==f.Clock.Value.AddDays(1),"Cron 触发一次，恢复检查不重复扫描");
    }
}
