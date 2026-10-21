using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

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
        var recentSuccess=new ExecutionRecord(Guid.NewGuid().ToString("N"),"crs.national-team","market-activity-sync",
            now.AddMinutes(-12),now.AddMinutes(-11),now.AddMinutes(-10),"Succeeded","rows=12",1);
        var recentFailed=new ExecutionRecord(Guid.NewGuid().ToString("N"),"crs.national-team","official-share-sync",
            now.AddMinutes(-8),now.AddMinutes(-7),now.AddMinutes(-6),"RetryDeferred","sourceIssues=1",1);
        var snapshot=ShellStatusBarViewModel.Describe(normal,[recentFailed,recentSuccess],2,true);
        Check(snapshot.HealthTone=="Success" && snapshot.HealthLabel=="服务正常","运行中的服务显示实时绿色健康状态");
        Check(snapshot.TaskLabel=="运行 2 · 排队 1" && snapshot.TaskDetail.Contains("持久手动命令 3"),
            "调度运行与排队数使用 Worker 原值，不把手动命令重复累加");
        Check(snapshot.AttentionLabel=="运行异常 0" && snapshot.AttentionDetail.Contains("不含税务/ETF数据缺口"),
            "零运行告警只代表来源和插件健康，不声称零业务待办");
        Check(snapshot.LastSyncLabel.StartsWith("最近同步 ") && snapshot.LastSyncDetail.Contains("market-activity-sync"),
            "最近成功同步不被更新的失败执行冒充");
        Check(snapshot.LastSyncDetail.Contains("不代表已取得当天交易数据"),"任务完成时间不冒充交易数据最新日期");

        var cooling=normal with
        {
            Health=normal.Health! with{State="Degraded",Sources=[new("SSE","Cooldown",0,0,now,now.AddHours(1),2,0,0)]},
            Plugins=[new("crs.national-team","Failed","Fixture")]
        };
        var blocked=ShellStatusBarViewModel.Describe(cooling,null,0,false);
        Check(blocked.HealthTone=="Warning" && blocked.AttentionLabel=="运行异常 2 项",
            "当前来源冷却与插件失败累计，绝不按正常状态展示");
        Check(blocked.LastSyncLabel=="最近同步：未确认","历史尚未查询时不沿用旧成功记录");

        var unknownSources=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{Sources=null}},null,0,false);
        Check(unknownSources.AttentionLabel=="运行异常：未确认","健康快照缺少来源字段时不以零异常冒充");

        var bad=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{State="StorageFault"}},null,0,false);
        Check(bad.HealthTone=="Error" && bad.AttentionLabel=="服务故障 · 查看","数据库故障保持故障语义");
        var disconnected=ShellStatusBarViewModel.Describe(normal with{Code="ServiceUnavailable"},[recentSuccess],1,true);
        Check(disconnected.HealthTone=="Error" && disconnected.TaskLabel.Contains("--") &&
              disconnected.LastSyncLabel.EndsWith("未确认") && disconnected.AttentionLabel.EndsWith("未确认"),
            "断线后撤回旧任务数、旧成功同步和旧零异常");
        var beyondSample=ShellStatusBarViewModel.Describe(normal,[recentFailed],1,true);
        Check(beyondSample.LastSyncLabel=="近1条未见成功同步","有限历史窗口不声称从未同步");
        var noScheduling=ShellStatusBarViewModel.Describe(normal with{Health=normal.Health! with{Scheduling=null}},null,0,false);
        Check(noScheduling.TaskLabel=="运行 -- · 排队 --","调度信息缺失时不显示虚构零");
    }
}
