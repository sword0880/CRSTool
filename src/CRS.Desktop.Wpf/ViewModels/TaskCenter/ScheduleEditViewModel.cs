using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Abstractions;
using CRS.PluginHost;

namespace CRS.DesktopClient.ViewModels;

/// <summary>编辑副本始终绑定打开时的任务及修订号；取消不会改变主页面。</summary>
public partial class ScheduleEditViewModel : ObservableObject
{
    private readonly BackgroundServiceViewModel parent;
    private TaskDefinition plan;
    public string TaskLabel=>plan.Name;
    public string Identity=>plan.PluginId+" / "+plan.JobId;
    public CancellationToken Lifetime=>parent.Lifetime;
    [ObservableProperty] private string cron;
    [ObservableProperty] private string timeZoneId;
    [ObservableProperty] private CronFrequency frequency;
    [ObservableProperty] private int interval=5;
    [ObservableProperty] private int hour=23;
    [ObservableProperty] private int minute=15;
    [ObservableProperty] private int second;
    [ObservableProperty] private int day=1;
    [ObservableProperty] private string description="";
    [ObservableProperty] private string validation="";
    [ObservableProperty] private bool valid;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(EnabledText)),NotifyPropertyChangedFor(nameof(NextTimesCaption)),NotifyCanExecuteChangedFor(nameof(EnableTaskCommand)),NotifyCanExecuteChangedFor(nameof(DisableTaskCommand))]
    private bool enabled;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(EnabledText)),NotifyPropertyChangedFor(nameof(NextTimesCaption)),NotifyPropertyChangedFor(nameof(CanEdit)),NotifyCanExecuteChangedFor(nameof(SaveCommand)),NotifyCanExecuteChangedFor(nameof(EnableTaskCommand)),NotifyCanExecuteChangedFor(nameof(DisableTaskCommand))]
    private bool enabledStatusKnown=true;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(CanEdit)),NotifyCanExecuteChangedFor(nameof(SaveCommand)),NotifyCanExecuteChangedFor(nameof(EnableTaskCommand)),NotifyCanExecuteChangedFor(nameof(DisableTaskCommand))]
    private bool busy;
    public bool CanEdit=>!Busy&&EnabledStatusKnown&&!Lifetime.IsCancellationRequested;
    public string EnabledText=>!EnabledStatusKnown?"启用状态未确认":Enabled?"已启用":"已禁用";
    public string NextTimesCaption=>!EnabledStatusKnown?"计划触发时间（启用状态未确认）":Enabled?"未来 5 次计划触发时间":"启用后的 5 次计划触发时间";
    public record FrequencyChoice(CronFrequency Value,string Label);
    public IReadOnlyList<FrequencyChoice> Frequencies {get;}=[new(CronFrequency.Seconds,"每隔若干秒"),new(CronFrequency.Minutes,"每隔若干分钟"),new(CronFrequency.Hourly,"每小时"),new(CronFrequency.Daily,"每天"),new(CronFrequency.Weekly,"每周"),new(CronFrequency.Monthly,"每月"),new(CronFrequency.Manual,"手动表达式")];
    public int[] Hours {get;}=Enumerable.Range(0,24).ToArray();
    public int[] Minutes {get;}=Enumerable.Range(0,60).ToArray();
    public int[] Intervals {get;}=Enumerable.Range(1,59).ToArray();
    public int[] Days {get;}=Enumerable.Range(1,31).ToArray();
    public System.Collections.ObjectModel.ObservableCollection<string> NextTimes {get;}=[];
    public IReadOnlyList<WeekdayChoice> Weekdays {get;}
    public bool ShowInterval=>Frequency is CronFrequency.Seconds or CronFrequency.Minutes;
    public bool ShowHour=>Frequency is CronFrequency.Daily or CronFrequency.Weekly or CronFrequency.Monthly;
    public bool ShowMinute=>Frequency is CronFrequency.Hourly || ShowHour;
    public bool ShowSecond=>Frequency is not (CronFrequency.Seconds or CronFrequency.Manual);
    public bool ShowWeekdays=>Frequency==CronFrequency.Weekly;
    public bool ShowDay=>Frequency==CronFrequency.Monthly;
    public bool IsManual=>Frequency==CronFrequency.Manual;
    private bool synchronizing=true;
    private string? generationError;
    [ObservableProperty] private string status="修改计划需要管理员权限；保存时由后台服务校验表达式和修订号。";
    public event Action? Saved;
    public ScheduleEditViewModel(BackgroundServiceViewModel parent,TaskDefinition plan)
    {
        this.parent=parent;this.plan=plan;enabled=plan.Enabled;cron=plan.Cron;timeZoneId=CRS.Timing.BeijingClock.ZoneId;
        if(plan.TimeZoneId is not ("China Standard Time" or "Asia/Shanghai"))status="保存后本计划按北京时间执行；保存前保留已有调度规则。";
        var options=CronEditor.Read(cron);frequency=options.Frequency;interval=options.Interval;hour=options.Hour;minute=options.Minute;second=options.Second;day=options.Day;
        var labels=new[]{"周一","周二","周三","周四","周五","周六","周日"};
        Weekdays=CronEditor.DayNames.Select((code,index)=>new WeekdayChoice(code,labels[index],(options.Frequency==CronFrequency.Weekly?options.Weekdays:"MON").Split(',').Contains(code))).ToArray();
        foreach(var item in Weekdays)item.PropertyChanged+=(_,_)=>Generate();
        synchronizing=false;UpdatePreview();
    }
    private CronOptions Options()=>new(Frequency,Interval,Hour,Minute,Second,Day,string.Join(',',Weekdays.Where(x=>x.Selected).Select(x=>x.Code)));
    partial void OnFrequencyChanged(CronFrequency value)
    {
        foreach(var name in new[]{nameof(ShowInterval),nameof(ShowHour),nameof(ShowMinute),nameof(ShowSecond),nameof(ShowWeekdays),nameof(ShowDay),nameof(IsManual)})OnPropertyChanged(name);
        Generate();
    }
    partial void OnIntervalChanged(int value)=>Generate();
    partial void OnHourChanged(int value)=>Generate();
    partial void OnMinuteChanged(int value)=>Generate();
    partial void OnSecondChanged(int value)=>Generate();
    partial void OnDayChanged(int value)=>Generate();
    partial void OnTimeZoneIdChanged(string value){if(!synchronizing)UpdatePreview();}
    partial void OnCronChanged(string value){if(!synchronizing)UpdatePreview();}
    private void Generate()
    {
        if(synchronizing)return;
        generationError=null;
        if(IsManual){UpdatePreview();return;}
        try
        {
            synchronizing=true;Cron=CronEditor.Generate(Options());
        }
        catch(ArgumentException e){generationError=e.Message;Description=e.Message;Valid=false;Validation=e.Message;NextTimes.Clear();SaveCommand.NotifyCanExecuteChanged();return;}
        finally{synchronizing=false;}
        UpdatePreview();
    }
    private void UpdatePreview()
    {
        Description=CronEditor.Describe(Options());
        if(generationError is not null){Valid=false;Validation=generationError;NextTimes.Clear();SaveCommand.NotifyCanExecuteChanged();return;}
        if(TimeZoneId!=CRS.Timing.BeijingClock.ZoneId){Valid=false;Validation="任务计划统一使用北京时间（UTC+08:00）。";NextTimes.Clear();SaveCommand.NotifyCanExecuteChanged();return;}
        var preview=CronEditor.Preview(Cron,TimeZoneId,CRS.Timing.BeijingClock.Now);
        Valid=preview.Valid;Validation=preview.Message;NextTimes.Clear();
        foreach(var instant in preview.Next)NextTimes.Add(instant.ToString("yyyy-MM-dd ddd HH:mm:ss zzz",System.Globalization.CultureInfo.GetCultureInfo("zh-CN")));
        SaveCommand.NotifyCanExecuteChanged();
    }
    private bool CanEnable()=>CanEdit&&!Enabled;
    private bool CanDisable()=>CanEdit&&Enabled;
    [RelayCommand(CanExecute=nameof(CanEnable))]
    private Task EnableTaskAsync()=>SetEnabledAsync(true);
    [RelayCommand(CanExecute=nameof(CanDisable))]
    private Task DisableTaskAsync()=>SetEnabledAsync(false);
    private async Task SetEnabledAsync(bool value)
    {
        if(!CanEdit||Enabled==value)return;
        Busy=true;
        try
        {
            var result=await parent.SetScheduleEnabledAsync(plan,value);
            Status=result.Message;
            if(result.Saved)
            {
                // 成功回执确认本修订已提交；优先采用刷新后的服务计划，后续保存仍受修订号保护。
                plan=parent.Jobs.FirstOrDefault(x=>x.PluginId==plan.PluginId&&x.JobId==plan.JobId&&x.Revision>plan.Revision)
                    ??plan with{Enabled=value,Revision=plan.Revision+1};
                Enabled=plan.Enabled;
                Status=(Enabled?"任务已启用。":"任务已禁用。")+" 执行频率的编辑内容仍需点击“保存计划”。 "+result.Message;
            }
            else if(parent.UncertainRequest is {} request&&request.PluginId==plan.PluginId&&request.JobId==plan.JobId)
            {EnabledStatusKnown=false;Status="启用状态未确认，请关闭详情，在任务中心点击“重新确认请求”后重新打开。";}
        }
        finally{Busy=false;}
    }
    private bool CanSave()=>Valid&&CanEdit;
    [RelayCommand(CanExecute=nameof(CanSave))]
    private async Task SaveAsync()
    {
        if(!CanSave())return;
        UpdatePreview();
        if(!Valid){Status=Validation;return;}
        Busy=true;
        try
        {
            var result=await parent.SaveScheduleAsync(plan,Cron.Trim(),TimeZoneId.Trim());
            Status=result.Message;
            if(result.Saved)Saved?.Invoke();
        }
        finally{Busy=false;}
    }
}

public partial class WeekdayChoice(string code,string label,bool initialSelected) : ObservableObject
{
    public string Code {get;}=code;
    public string Label {get;}=label;
    [ObservableProperty] private bool selected=initialSelected;
}
