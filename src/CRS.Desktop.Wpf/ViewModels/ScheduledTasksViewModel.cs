using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.PluginHost;

namespace CRS.DesktopClient.ViewModels;

public sealed record ScheduleFrequencyOption(ScheduleFrequency Value,string Text);
/// <summary>只做任务状态格式化；触发时间、持久化和执行控制由主程序调度服务负责。</summary>
public sealed record ScheduledTaskRow(TaskPlanView Value)
{
    public string Name=>Value.Plan.Name;
    public string Plugin=>Value.Plan.PluginId;
    public string State=>Value.ErrorCode=="HISTORY_WRITE_FAILED"?"执行记录异常":Value.ErrorCode is not null?"计划应用失败":!Value.Registered?"插件未启动":Value.Running?"正在执行":Value.Queued?"等待执行":Value.Plan.Enabled?"已启用":"已暂停";
    public string Next=>Format(Value.NextUtc);
    public string Last=>Value.LastRun is null?"尚无记录":Outcome(Value.LastRun.State);
    public static string Format(DateTimeOffset? time)=>time is null?"—":TimeZoneInfo.ConvertTimeBySystemTimeZoneId(time.Value,"China Standard Time").ToString("yyyy-MM-dd HH:mm:ss");
    public static string Outcome(TaskRunState state)=>state switch{TaskRunState.Running=>"运行中",TaskRunState.Succeeded=>"成功",TaskRunState.Cancelled=>"已取消",TaskRunState.Interrupted=>"上次运行中断",TaskRunState.Incomplete=>"部分完成",TaskRunState.Skipped=>"未执行",_=>"失败"};
}
public sealed record ScheduledTaskRunRow(TaskRun Value)
{
    public string Plugin=>Value.PluginId;
    public string Task=>Value.TaskId;
    public string Started=>ScheduledTaskRow.Format(Value.StartedUtc);
    public string Ended=>ScheduledTaskRow.Format(Value.EndedUtc);
    public string Result=>ScheduledTaskRow.Outcome(Value.State);
    public string Origin=>Value.Origin switch{"Manual"=>"立即执行","Startup"=>"启动同步",_=>"计划触发"};
    public string? Code=>Value.FailureCode;
}
/// <summary>任务管理只管理交互、输入和展示，不在前台解析Cron或执行采集。</summary>
public partial class ScheduledTasksViewModel(QuartzTaskScheduler scheduler,CancellationToken lifetime):ObservableObject
{
    public ObservableCollection<ScheduledTaskRow> Tasks {get;}=[];
    public ObservableCollection<ScheduledTaskRunRow> History {get;}=[];
    public IReadOnlyList<ScheduleFrequencyOption> Frequencies {get;}=[new(ScheduleFrequency.Daily,"每天"),new(ScheduleFrequency.Weekdays,"周一至周五"),new(ScheduleFrequency.Hourly,"每小时"),new(ScheduleFrequency.Custom,"高级计划（仅可启停）")];
    [ObservableProperty] private ScheduledTaskRow? selected;
    [ObservableProperty] private ScheduleFrequencyOption? frequency;
    [ObservableProperty] private string hour="23";
    [ObservableProperty] private string minute="15";
    [ObservableProperty] private bool enabled=true;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status="计划按北京时间运行；关闭或锁定软件后暂停，重新解锁后恢复已保存设置。";
    private bool CanAct()=>Selected is not null && !Busy && !lifetime.IsCancellationRequested;
    private bool CanSave()=>CanAct() && Frequency?.Value is not (null or ScheduleFrequency.Custom);
    private void NotifyCommands(){SaveCommand.NotifyCanExecuteChanged();ToggleCommand.NotifyCanExecuteChanged();RunNowCommand.NotifyCanExecuteChanged();CancelRunCommand.NotifyCanExecuteChanged();}
    partial void OnBusyChanged(bool value)=>NotifyCommands();
    partial void OnFrequencyChanged(ScheduleFrequencyOption? value)=>NotifyCommands();
    partial void OnSelectedChanged(ScheduledTaskRow? value)
    {
        if(value is not null){Frequency=Frequencies.Single(x=>x.Value==value.Value.Frequency);Hour=value.Value.Hour.ToString(System.Globalization.CultureInfo.InvariantCulture);Minute=value.Value.Minute.ToString(System.Globalization.CultureInfo.InvariantCulture);Enabled=value.Value.Plan.Enabled;}
        NotifyCommands();
    }
    private async Task ExecuteAsync(Func<CancellationToken,Task> work)
    {
        if(Busy || lifetime.IsCancellationRequested)return;Busy=true;
        try{await work(lifetime);}
        catch(OperationCanceledException){Status="操作已取消。";}
        catch(ArgumentException error){Status=error.Message;}
        catch(InvalidOperationException error){Status=error.Message;}
        catch(Exception error){Status="任务管理操作失败；类别："+error.GetType().Name;}
        finally{Busy=false;}
    }
    public async Task LoadAsync(CancellationToken token)
    {
        var selectedKey=Selected?.Value.Plan;var rows=await scheduler.ListAsync(token);var history=await scheduler.HistoryAsync(token);token.ThrowIfCancellationRequested();
        Tasks.Clear();foreach(var row in rows)Tasks.Add(new(row));
        History.Clear();foreach(var run in history)History.Add(new(run));
        Selected=Tasks.FirstOrDefault(x=>x.Value.Plan.PluginId==selectedKey?.PluginId && x.Value.Plan.TaskId==selectedKey.TaskId)??Tasks.FirstOrDefault();
    }
    [RelayCommand] private Task Refresh()=>ExecuteAsync(async token=>{await LoadAsync(token);Status="状态已刷新；记录保留最近500次执行。";});
    [RelayCommand(CanExecute=nameof(CanSave))] private Task Save()=>ExecuteAsync(async token=>
    {
        var plan=Selected!.Value.Plan;await scheduler.ConfigureSimpleTextAsync(plan.PluginId,plan.TaskId,Frequency!.Value,Hour,Minute,Enabled,token);
        await LoadAsync(token);Status="计划已保存，即时生效；编辑会取消本次尚未执行的启动同步。";
    });
    [RelayCommand(CanExecute=nameof(CanAct))] private Task Toggle()=>ExecuteAsync(async token=>
    {
        var plan=Selected!.Value.Plan;await scheduler.SetEnabledAsync(plan.PluginId,plan.TaskId,!plan.Enabled,token);await LoadAsync(token);
        Status=plan.Enabled?"已暂停后续自动触发；正在执行的任务可单独取消。":"已恢复计划，将按下一计划时间执行。";
    });
    [RelayCommand(CanExecute=nameof(CanAct))] private Task RunNow()=>ExecuteAsync(async token=>
    {
        var plan=Selected!.Value.Plan;await scheduler.RunNowAsync(plan.PluginId,plan.TaskId,token);await LoadAsync(token);Status="已交由Quartz执行；点击刷新查看进度与结果。";
    });
    [RelayCommand(CanExecute=nameof(CanAct))] private Task CancelRun()=>ExecuteAsync(async token=>
    {
        var plan=Selected!.Value.Plan;await scheduler.CancelRunAsync(plan.PluginId,plan.TaskId,token);await LoadAsync(token);Status="已请求取消；已提交的研究事实保留，请刷新确认结果。";
    });
}
