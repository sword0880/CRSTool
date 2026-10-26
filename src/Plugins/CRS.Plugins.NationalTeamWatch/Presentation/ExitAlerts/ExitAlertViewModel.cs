using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>预警中心只维护筛选、选择及编辑草稿，计算、原件、数据库和投递均在后台。</summary>
public partial class ExitAlertViewModel(IExitAlertWorkspace workspace,IEtfResearchWorkspace etfWorkspace,CancellationToken lifetime):ObservableObject
{
    internal static IExitAlertWorkspace AsPort(IEtfWorkspace workspace)=>workspace as IExitAlertWorkspace??new LegacyExitAlertWorkspaceAdapter(workspace);
    /// <summary>Compatibility constructor for existing consumers of the legacy composite workspace contract.</summary>
    public ExitAlertViewModel(IEtfWorkspace workspace,CancellationToken lifetime)
        :this(AsPort(workspace),LegacyEtfResearchWorkspaceAdapter.Create(workspace),lifetime) {}
    public bool CanManage=>workspace.CanManage;
    public bool CanEdit=>CanManage&&!Busy;
    public ObservableCollection<ExitAlertEvent> Events {get;}=[];
    public ObservableCollection<ExitAlertChange> Changes {get;}=[];
    public ObservableCollection<EvidenceRow> Inputs {get;}=[];
    public ObservableCollection<AlertOutboxItem> Notifications {get;}=[];
    public ObservableCollection<AlertDeliveryRecord> Deliveries {get;}=[];
    public ObservableCollection<AlertRouteEditor> Routes {get;}=[];
    public IReadOnlyList<string> Lanes {get;}=["全部","数据核查","ETF份额异常","机构最低净减少证据"];
    public IReadOnlyList<string> States {get;}=["全部","待核实","持续关注","已解除","修订撤销","历史证明保留","规则版本已替换"];
    [ObservableProperty] private string lane="全部";
    [ObservableProperty] private string state="全部";
    [ObservableProperty] private string search="";
    [ObservableProperty] private DateTime? from=Today.AddDays(-90);
    [ObservableProperty] private DateTime? to=Today;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status="由后台评价公开日终数据；尚未通过正式数据门槛时只作研究预览。";
    [ObservableProperty] private string overviewText="尚未读取服务状态";
    [ObservableProperty] private string detail="选择事件查看输入证据、时间与变更记录。";
    [ObservableProperty] private ExitAlertEvent? selectedEvent;
    [ObservableProperty] private ExitAlertChange? selectedChange;
    [ObservableProperty] private AlertOutboxItem? selectedNotification;
    [ObservableProperty] private DateTime? notificationFrom=Today.AddDays(-31);
    [ObservableProperty] private DateTime? notificationTo=Today;
    [ObservableProperty] private string notificationState="全部";
    [ObservableProperty] private string notificationSearch="";
    public IReadOnlyList<string> NotificationStates {get;}=["全部","待发送","已受理","等待重试","未启用","不可用","失败","受理未知","门槛阻断","已被替代"];
    [ObservableProperty] private string ruleVersion="exit-v1.0";
    [ObservableProperty] private bool rulesEnabled;
    [ObservableProperty] private decimal decline5Percent=5;
    [ObservableProperty] private decimal decline20Percent=10;
    [ObservableProperty] private int minimumFunds=3;
    [ObservableProperty] private int minimumManagers=2;
    [ObservableProperty] private int clearObservations=2;
    [ObservableProperty] private bool notifyWatch;
    [ObservableProperty] private bool notifyResolved=true;
    private ExitAlertConfiguration? savedConfiguration;
    private int detailVersion;
    private int deliveryVersion;
    public Task DetailLoadTask {get;private set;}=Task.CompletedTask;
    public Task DeliveryLoadTask {get;private set;}=Task.CompletedTask;
    private static DateTime Today=>CRS.Timing.BeijingClock.Now.Date;
    partial void OnBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEdit));
    partial void OnSelectedEventChanged(ExitAlertEvent? value)=>DetailLoadTask=LoadDetailAsync(value,++detailVersion);
    partial void OnSelectedChangeChanged(ExitAlertChange? value)
    {
        if(value is not null&&SelectedEvent is {} current)DetailLoadTask=LoadDetailAsync(current,++detailVersion,value.Sequence);
    }
    partial void OnSelectedNotificationChanged(AlertOutboxItem? value)=>DeliveryLoadTask=LoadDeliveryAsync(value,++deliveryVersion);
    private async Task Run(Func<Task> action)
    {
        if(Busy||lifetime.IsCancellationRequested)return;Busy=true;
        try{await action();}
        catch(OperationCanceledException){Status="已停止等待；已受理的后台任务可在任务管理查看。";}
        catch(Exception error){Status=error is InvalidDataException?error.Message:"预警操作未完成："+error.GetType().Name;}
        finally{Busy=false;}
    }
    private ExitAlertFilter Filter()=>new(Lane switch{"数据核查"=>"DataQuality","ETF份额异常"=>"EtfOutflow","机构最低净减少证据"=>"InstitutionEvidence",_=>null},
        State switch{"待核实"=>"PendingVerification","持续关注"=>"Active","已解除"=>"Resolved","修订撤销"=>"RetractedByRevision","历史证明保留"=>"HistoricalEvidence","规则版本已替换"=>"RuleSuperseded",_=>null},
        From is {} from?DateOnly.FromDateTime(from):null,To is {} to?DateOnly.FromDateTime(to):null,Search);
    [RelayCommand] private Task Refresh()=>Run(async()=>
    {
        await LoadEventsAsync();await LoadSettingsAsync();await LoadProofFundsAsync();
        Status="已读取后台事实。数据核查、份额异常和机构证明分别列示；没有行情证据不生成Critical。";
    });
    [RelayCommand] private Task QueryEvents()=>Run(LoadEventsAsync);
    private async Task LoadEventsAsync()
    {
        var filter=Filter();SelectedEvent=null;Events.Clear();
        var rows=await workspace.AlertEventsAsync(filter,lifetime);var overview=await workspace.AlertOverviewAsync(lifetime);
        // 编辑筛选期间返回的旧快照不能显示为新条件的结果。
        if(filter!=Filter()){Status="筛选已改变，请重新查询。";return;}
        foreach(var row in rows)Events.Add(row);
        OverviewText=$"监控{overview.MonitoredFunds}只 · 已核验可比口径{overview.ComparableFunds}只 · 待核事实{overview.PendingFacts}条 · 活跃事件{overview.ActiveEvents}条\n"
            +(overview.DataAcceptancePassed?"真实数据门槛通过":"研究预览：真实250日或连续20交易日等门槛尚未全部通过，金融预警禁止外发。")+"\n"+overview.MarketRiskState;
    }
    private async Task LoadSettingsAsync()
    {
        savedConfiguration=await workspace.AlertConfigurationAsync(lifetime);var rules=savedConfiguration.Rules;
        RuleVersion=rules.Version;RulesEnabled=rules.Enabled;Decline5Percent=rules.Decline5*100;Decline20Percent=rules.Decline20*100;
        MinimumFunds=rules.MinimumFunds;MinimumManagers=rules.MinimumManagers;ClearObservations=rules.ClearObservations;NotifyWatch=rules.NotifyWatch;NotifyResolved=rules.NotifyResolved;
        var channels=await workspace.AlertChannelsAsync(lifetime);Routes.Clear();
        foreach(var route in savedConfiguration.Routes)
            Routes.Add(new(route.ChannelId,channels.FirstOrDefault(x=>x.Id==route.ChannelId)?.Name??"未安装渠道",route.ProfileId,true));
        foreach(var channel in channels.Where(x=>!Routes.Any(r=>r.ChannelId==x.Id)))Routes.Add(new(channel.Id,channel.Name,"default",false));
    }
    [RelayCommand] private Task SaveRules()=>Run(async()=>
    {
        if(!CanManage||savedConfiguration is null)throw new InvalidDataException("请先读取规则并使用管理员权限。");
        var rules=new ExitAlertRules(RuleVersion,RulesEnabled,Decline5Percent/100,Decline20Percent/100,MinimumFunds,MinimumManagers,ClearObservations,NotifyWatch,NotifyResolved);
        await workspace.SaveAlertConfigurationAsync(new(rules,Routes.Where(x=>x.Selected).Select(x=>new AlertNotificationRoute(x.ChannelId,x.ProfileId)).ToArray(),savedConfiguration.Revision),lifetime);
        await LoadSettingsAsync();await LoadEventsAsync();Status="规则及订阅已保存；更改规则内容需新版本号。保存不会发送测试通知。";
    });
    [RelayCommand] private Task Evaluate()=>Run(async()=>{await workspace.EvaluateAlertsAsync(lifetime);await LoadEventsAsync();await LoadNotificationsAsync();Status="后台评价完成；通知中心单独展示每个渠道的提交状态。";});
    [RelayCommand] private Task RefreshNotifications()=>Run(LoadNotificationsAsync);
    private async Task LoadNotificationsAsync()
    {
        var filter=new ExitAlertFilter(State:NotificationState switch{"待发送"=>"Pending","已受理"=>"Accepted","等待重试"=>"Retry","未启用"=>"Disabled","不可用"=>"Unavailable","失败"=>"Failed","受理未知"=>"OutcomeUnknown","门槛阻断"=>"BlockedAcceptance","已被替代"=>"Superseded",_=>null},
            From:NotificationFrom is {} from?DateOnly.FromDateTime(from):null,To:NotificationTo is {} to?DateOnly.FromDateTime(to):null,Search:NotificationSearch);
        SelectedNotification=null;Notifications.Clear();foreach(var row in await workspace.AlertOutboxAsync(filter,lifetime))Notifications.Add(row);
    }
    [RelayCommand] private Task RetryNotification()=>Run(async()=>
    {
        if(!CanManage||SelectedNotification is null)return;
        await workspace.RetryAlertNotificationAsync(SelectedNotification.Id,lifetime);await LoadNotificationsAsync();Status="已安排安全重试；未知受理、已受理和正式门槛阻断的记录不能通过此入口重发。";
    });
    private async Task LoadDetailAsync(ExitAlertEvent? row,int version,int? sequence=null)
    {
        Detail="";Inputs.Clear();if(sequence is null){SelectedChange=null;Changes.Clear();}if(row is null)return;
        try
        {
            var current=sequence is {} number?await workspace.AlertEventVersionAsync(row.Id,number,lifetime):await workspace.AlertEventAsync(row.Id,lifetime);
            var changes=sequence is null?await workspace.AlertChangesAsync(row.Id,lifetime):null;var inputs=await workspace.AlertInputsAsync(row.Id,sequence,lifetime);
            if(version!=detailVersion||lifetime.IsCancellationRequested)return;
            Detail=(sequence is {} historical?$"历史变更版本{historical}（按该次记录展示，不代表当前状态）\n":"")+ $"{current.Headline}\n{current.LaneText} · {current.SeverityText} · {current.StateText} · {current.DataHealthText}\n{current.UsageText}\n规则：{current.RuleVersion}；证据类型：{current.EvidenceText}\n"
                +$"首次发现：{current.FirstSeenAt}；最近更新：{current.UpdatedAt}\n观察区间：{current.FirstTradeDate:yyyy-MM-dd} 至 {current.LastTradeDate:yyyy-MM-dd}\n{current.Explanation}\n{current.HistoricalRevisionNote}\n输入证据见下表（更正时同时保留原修订和新输入）。";
            if(changes is not null)foreach(var change in changes)Changes.Add(change);
            foreach(var input in inputs)Inputs.Add(input);
        }
        catch(OperationCanceledException){}catch(Exception error){if(version==detailVersion)Detail="详情读取失败："+error.GetType().Name;}
    }
    private async Task LoadDeliveryAsync(AlertOutboxItem? row,int version)
    {
        Deliveries.Clear();if(row is null)return;
        try{var rows=await workspace.AlertDeliveriesAsync(row.Id,lifetime);if(version==deliveryVersion&&!lifetime.IsCancellationRequested)foreach(var item in rows)Deliveries.Add(item);}
        catch(OperationCanceledException){}catch(Exception error){if(version==deliveryVersion)Status="提交记录读取失败："+error.GetType().Name;}
    }
}

/// <summary>只编辑渠道订阅引用，凭据和实际收件目标留在系统设置的各渠道配置中。</summary>
public partial class AlertRouteEditor(string channelId,string name,string profileId,bool selected):ObservableObject
{
    public string ChannelId {get;}=channelId;
    public string Name {get;}=name;
    [ObservableProperty] private string profileId=profileId;
    [ObservableProperty] private bool selected=selected;
}


/// <summary>Adapts the unchanged legacy composite contract to the narrower exit-alert port.</summary>
internal sealed class LegacyExitAlertWorkspaceAdapter(IEtfWorkspace inner):IExitAlertWorkspace
{
    public bool CanManage=>inner.CanManage;
    public Task<ExitAlertOverview> AlertOverviewAsync(CancellationToken token)=>inner.AlertOverviewAsync(token);
    public Task<ExitAlertConfiguration> AlertConfigurationAsync(CancellationToken token)=>inner.AlertConfigurationAsync(token);
    public Task SaveAlertConfigurationAsync(ExitAlertConfiguration value,CancellationToken token)=>inner.SaveAlertConfigurationAsync(value,token);
    public Task<List<ExitAlertEvent>> AlertEventsAsync(ExitAlertFilter filter,CancellationToken token)=>inner.AlertEventsAsync(filter,token);
    public Task<ExitAlertEvent> AlertEventAsync(string id,CancellationToken token)=>inner.AlertEventAsync(id,token);
    public Task<ExitAlertEvent> AlertEventVersionAsync(string id,int sequence,CancellationToken token)=>inner.AlertEventVersionAsync(id,sequence,token);
    public Task<List<EvidenceRow>> AlertInputsAsync(string id,int? sequence,CancellationToken token)=>inner.AlertInputsAsync(id,sequence,token);
    public Task<List<ExitAlertChange>> AlertChangesAsync(string id,CancellationToken token)=>inner.AlertChangesAsync(id,token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(CancellationToken token)=>inner.AlertOutboxAsync(token);
    public Task<List<AlertOutboxItem>> AlertOutboxAsync(ExitAlertFilter filter,CancellationToken token)=>inner.AlertOutboxAsync(filter,token);
    public Task<List<AlertDeliveryRecord>> AlertDeliveriesAsync(string id,CancellationToken token)=>inner.AlertDeliveriesAsync(id,token);
    public Task RetryAlertNotificationAsync(string id,CancellationToken token)=>inner.RetryAlertNotificationAsync(id,token);
    public Task EvaluateAlertsAsync(CancellationToken token)=>inner.EvaluateAlertsAsync(token);
    public Task<List<AlertChannel>> AlertChannelsAsync(CancellationToken token)=>inner.AlertChannelsAsync(token);
    public Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>inner.AlertDocumentsAsync(key,token);
    public Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token)=>inner.RegisterAlertDocumentAsync(request,token);
    public Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>inner.AlertMetadataAsync(token);
    public Task ReviewAlertMetadataAsync(AlertMetadataReview review,CancellationToken token)=>inner.ReviewAlertMetadataAsync(review,token);
    public Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>inner.NamedEtfHoldingsAsync(key,token);
    public Task ReviewNamedEtfHoldingAsync(NamedEtfHoldingReview review,CancellationToken token)=>inner.ReviewNamedEtfHoldingAsync(review,token);
}
