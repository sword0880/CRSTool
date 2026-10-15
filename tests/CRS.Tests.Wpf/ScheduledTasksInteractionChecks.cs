using CRS.DesktopClient.ViewModels;
using CRS.PluginHost;
/// <summary>计划编辑的前台只提交输入，真实调度服务负责持久化和状态。</summary>
internal static class ScheduledTasksInteractionChecks
{
    public static async Task RunAsync()
    {
        await using var scheduler=new QuartzTaskScheduler();using var lifetime=new CancellationTokenSource();
        await scheduler.Scope("fixture",lifetime.Token).RegisterAsync("sync","0 15 23 * * ?","China Standard Time",_=>Task.CompletedTask,CancellationToken.None,displayName:"合成任务");
        var model=new ScheduledTasksViewModel(scheduler,lifetime.Token);await model.LoadAsync(lifetime.Token);
        if(model.Selected?.Name!="合成任务" || model.Hour!="23" || model.Minute!="15")throw new Exception("计划编辑未显示当前输入。");
        model.Hour="8";model.Minute="45";model.Enabled=false;await model.SaveCommand.ExecuteAsync(null);
        var changed=(await scheduler.ListAsync()).Single();
        if(changed.Plan.Enabled || changed.Hour!=8 || changed.Minute!=45 || model.Busy)throw new Exception("前台计划保存未提交到真实服务。");
        await model.RunNowCommand.ExecuteAsync(null);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while((await scheduler.HistoryAsync()).FirstOrDefault()?.State!=TaskRunState.Succeeded)await Task.Delay(20,timeout.Token);
        await model.RefreshCommand.ExecuteAsync(null);if(model.History.Single().Result!="成功")throw new Exception("执行结果未显示。");
        model.Hour="25";await model.SaveCommand.ExecuteAsync(null);
        if(!model.Status.Contains("小时") || (await scheduler.ListAsync()).Single().Hour!=8)throw new Exception("非法计划输入污染已有设置。");
        model.Hour="abc";await model.SaveCommand.ExecuteAsync(null);if(!model.Status.Contains("整数") || (await scheduler.ListAsync()).Single().Hour!=8)throw new Exception("非数字输入被悄悄当作旧值保存。");
        lifetime.Cancel();if(model.RunNowCommand.CanExecute(null))throw new Exception("锁定后仍可提交计划操作。");
        Console.WriteLine("计划任务交互通过：加载、保存、暂停手动执行、真实结果、非法输入及锁定门槛。");
    }
}
