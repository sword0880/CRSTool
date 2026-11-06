using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Maintenance;

/// <summary>纯状态映射回归；使用合成 Worker 快照，不会启动服务或读个人税务资料。</summary>
internal static class ShellStatusBarChecks
{
    private static void Check(bool condition,string scenario)
    {
        if(!condition)throw new InvalidOperationException("底部状态栏："+scenario);
        Console.WriteLine("PASS 底部状态栏："+scenario);
    }

    public static void Run()
    {
        var now=new DateTimeOffset(2026,10,21,13,0,0,TimeSpan.FromHours(8));
        var normal=new BackgroundResponse(WorkerProtocol.Version,Guid.NewGuid(),"Ok",
            new("Running",now.AddDays(-1),now,1234,
                new(4,2,1,12,0,0,3),
                [new("SSE","Ready",0,0,now,DateTimeOffset.MinValue,0,0,0)]),
            Plugins:[new("crs.national-team","Started")]);
        var activity=new BackgroundTaskActivity(now,[],2,1,2);
        var snapshot=ShellStatusBarViewModel.Describe(normal,activity:activity);
        Check(snapshot.HealthTone=="Success" && snapshot.HealthLabel=="服务正常","运行中的服务显示实时绿色健康状态");
        Check(snapshot.TaskLabel=="运行 2 · 等待 3" && snapshot.TaskDetail.Contains("同一快照已去重"),
            "底栏仅显示运行和等待，健康采样中重叠的手动请求不重复累加");
        Check(snapshot.AttentionLabel=="运行异常 0" && snapshot.AttentionDetail.Contains("不含税务/ETF数据缺口"),
            "零运行告警只代表来源和插件健康，不声称零业务待办");
        var cooling=normal with
        {
            Health=normal.Health! with{State="Degraded",Sources=[new("SSE","Cooldown",0,0,now,now.AddHours(1),2,0,0)]},
            Plugins=[new("crs.national-team","Failed","Fixture")]
        };
        var blocked=ShellStatusBarViewModel.Describe(cooling);
        Check(blocked.HealthTone=="Warning" && blocked.AttentionLabel=="运行异常 2 项",
            "当前来源冷却与插件失败累计，绝不按正常状态展示");

        var unknownSources=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{Sources=null}});
        Check(unknownSources.AttentionLabel=="运行异常：未确认","健康快照缺少来源字段时不以零异常冒充");

        var bad=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{State="StorageFault"}});
        Check(bad.HealthTone=="Error" && bad.AttentionLabel=="服务故障 · 查看","数据库故障保持故障语义");
        var disconnected=ShellStatusBarViewModel.Describe(normal with{Code="ServiceUnavailable"});
        Check(disconnected.HealthTone=="Error" && disconnected.TaskLabel.Contains("--") &&
              disconnected.AttentionLabel.EndsWith("未确认"),
            "断线后撤回旧任务数和旧零异常");
        var missingActivity=ShellStatusBarViewModel.Describe(normal);
        Check(missingActivity.TaskLabel=="运行 2 · 等待 --","缺少可去重快照时不直接相加旧排队与请求数量");
        var noScheduling=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{Scheduling=null}});
        Check(noScheduling.TaskLabel=="运行 -- · 等待 --","调度信息缺失时不显示虚构零");
        var cleanup=new DataCleanupTask(Guid.NewGuid(),"Manual","Queued",now,now,null,0,0,true,"已受理");
        var queued=ShellStatusBarViewModel.Describe(normal,cleanup,activity:activity);
        Check(queued.TaskLabel=="运行 2 · 等待 4","排队清理计入等待总数，底栏不单列清理名称");
        cleanup=cleanup with{State="Scanning",Progress=new("VerifyingBackups",null,859,119382731,VerifiedBytes:4096)};
        var scanning=ShellStatusBarViewModel.Describe(normal,cleanup,activity:activity);
        Check(scanning.TaskLabel=="运行 3 · 等待 3"&&scanning.TaskDetail.Contains("859")&&scanning.TaskDetail.Contains("独立于插件执行名额"),
            "扫描清理计入总运行数，底栏不单列阶段；实际："+scanning.TaskLabel+" / "+scanning.TaskDetail);
        var unknown=ShellStatusBarViewModel.Describe(normal,cleanup,false);
        Check(unknown.TaskLabel=="运行 -- · 等待 --"&&unknown.TaskDetail.Contains("未取得最新状态"),"清理断线时总运行数未确认，不把旧数或部分数量当作总数");
        var noPlugins=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{Scheduling=null}},cleanup);
        Check(noPlugins.TaskLabel=="运行 -- · 等待 --","清理摘要已确认时仍不虚构未知的插件总数");
        var done=ShellStatusBarViewModel.Describe(normal,cleanup with{State="Completed",FinishedAt=now},activity:activity);
        Check(done.TaskLabel==snapshot.TaskLabel,"完成任务不留在运行计数中");
        var lost=ShellStatusBarViewModel.Describe(normal with{Code="ServiceUnavailable"},cleanup);
        Check(!lost.TaskLabel.Contains("清理：")&&lost.TaskLabel.Contains("--"),"服务失联时撤回旧清理活动状态");
    }
}
