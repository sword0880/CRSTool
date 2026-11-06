using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using System.Windows.Threading;
using System.IO;
using CRS.Maintenance;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

/// <summary>
/// 只读桌面壳运行状态：不承接税务敏感数据、管理命令或第二套后台调度。
/// 采样来自独立 Worker，连接失败立即撤回旧绿色状态。
/// </summary>
public partial class ShellStatusBarViewModel : ObservableObject
{
    private readonly BackgroundPipeClient client;
    private readonly DataCleanupViewModel? cleanup;
    private DispatcherTimer? timer;
    private CancellationTokenSource? polling;
    private bool refreshing;
    private List<ExecutionRecord>? verifiedHistory;
    private long historySampleAt;
    private bool historyRefreshing;
    private BackgroundTaskActivity? verifiedActivity;
    private bool activityRefreshing;
    private BackgroundResponse? latestHealth;
    private long connectionRevision;

    public ShellStatusBarViewModel(BackgroundPipeClient? client = null,DataCleanupViewModel? cleanup = null)
    {
        this.client=client??new();this.cleanup=cleanup;
        if(cleanup is not null)cleanup.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(DataCleanupViewModel.CurrentTask) or nameof(DataCleanupViewModel.TaskStatusKnown)
                &&latestHealth is {} health)
                Update(DescribeCurrent(health));
        };
    }
    private ShellStatusSnapshot DescribeCurrent(BackgroundResponse health)=>Describe(health,verifiedHistory,verifiedHistory?.Count??0,
        verifiedHistory is not null,cleanup?.CurrentTask,cleanup?.TasksAvailable!=true||cleanup.TaskStatusKnown,verifiedActivity);

    [ObservableProperty] private string healthLabel = "后台状态待确认";
    [ObservableProperty] private string healthTone = "Warning";
    [ObservableProperty] private string healthDetail = "尚未取得独立 Windows Service 的健康采样。";
    [ObservableProperty] private string taskLabel = "运行 -- · 等待 --";
    [ObservableProperty] private string taskDetail = "尚未取得真实调度状态。";
    [ObservableProperty] private string lastSyncLabel = "最近同步：未确认";
    [ObservableProperty] private string lastSyncDetail = "仅统计国家队后台插件的成功同步，不推断数据最新交易日期。";
    [ObservableProperty] private string attentionLabel = "运行异常：未确认";
    [ObservableProperty] private string attentionDetail = "仅统计 Worker 的插件故障及官方来源冷却，不代表业务审核待办总数。";

    /// <summary>每3秒更新运行/排队状态；成功同步历史仍最多每45秒读取。</summary>
    public void Start(CancellationToken lifetime)
    {
        if(timer is not null || lifetime.IsCancellationRequested)return;
        polling=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(3)};
        timer.Tick+=OnTick;
        timer.Start();
        _=RefreshAsync();
    }

    public void Stop()
    {
        if(timer is not null){timer.Stop();timer.Tick-=OnTick;timer=null;}
        polling?.Cancel();polling?.Dispose();polling=null;
        verifiedHistory=null;verifiedActivity=null;latestHealth=null;
    }

    private async void OnTick(object? sender,EventArgs e)=>await RefreshAsync();

    private async Task RefreshAsync()
    {
        if(refreshing || polling is null || polling.IsCancellationRequested)return;
        refreshing=true;
        var token=polling.Token;
        try
        {
            var health=await client.QueryAsync("GetHealth",token);
            token.ThrowIfCancellationRequested();
            latestHealth=health;
            if(health.Code!="Ok" || health.Health?.State is not ("Running" or "Degraded" or "Maintenance"))
            {verifiedHistory=null;verifiedActivity=null;connectionRevision++;Update(DescribeCurrent(health));return;}
            Update(DescribeCurrent(health));
            if(!activityRefreshing)_=RefreshActivityAsync(token,connectionRevision);
            if(verifiedHistory is not null && Environment.TickCount64-historySampleAt<45000)return;
            if(!historyRefreshing)_=RefreshHistoryAsync(token,connectionRevision);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception)
        {
            if(!token.IsCancellationRequested){verifiedHistory=null;verifiedActivity=null;latestHealth=null;connectionRevision++;Update(Unavailable());}
        }
        finally{refreshing=false;}
    }
    private async Task RefreshActivityAsync(CancellationToken token,long revision)
    {
        activityRefreshing=true;
        try
        {
            var snapshot=await WorkerTaskActivityReader.ReadAsync(client,token);
            token.ThrowIfCancellationRequested();
            if(revision!=connectionRevision||latestHealth is null)return;
            verifiedActivity=snapshot;Update(DescribeCurrent(latestHealth));
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception)
        {
            if(revision==connectionRevision&&latestHealth is {} health)
            {verifiedActivity=null;Update(DescribeCurrent(health));}
        }
        finally{activityRefreshing=false;}
    }
    private async Task RefreshHistoryAsync(CancellationToken token,long revision)
    {
        historyRefreshing=true;
        try
        {
            // 一页最多10条；至多查询3页，避免频繁跨插件获取全部历史。
            // 任务中心可查看完整分页历史，此处只给出已核实范围的最近成功时间。
            var rows=new List<ExecutionRecord>();
            string? cursor=null;
            for(var page=0;page<3;page++)
            {
                var response=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",
                    PluginId:"crs.national-team",Cursor:cursor,PageSize:10),token);
                token.ThrowIfCancellationRequested();
                if(response.Code!="Ok" || response.History is null)
                    throw new InvalidDataException("同步历史读取失败。");
                rows.AddRange(response.History);
                cursor=response.NextCursor;
                if(cursor is null || rows.Any(IsSuccessfulSync))break;
            }
            if(revision!=connectionRevision || latestHealth is null)return;
            verifiedHistory=rows;historySampleAt=Environment.TickCount64;
            Update(DescribeCurrent(latestHealth));
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception)
        {
            if(!token.IsCancellationRequested && revision==connectionRevision)
            {
                verifiedHistory=null;
                if(latestHealth is {} health)Update(DescribeCurrent(health));
            }
        }
        finally{historyRefreshing=false;}
    }

    private void Update(ShellStatusSnapshot value)
    {
        HealthLabel=value.HealthLabel;HealthTone=value.HealthTone;HealthDetail=value.HealthDetail;
        TaskLabel=value.TaskLabel;TaskDetail=value.TaskDetail;
        LastSyncLabel=value.LastSyncLabel;LastSyncDetail=value.LastSyncDetail;
        AttentionLabel=value.AttentionLabel;AttentionDetail=value.AttentionDetail;
    }

    private static bool IsSuccessfulSync(ExecutionRecord item) =>
        item.Result=="Succeeded" && item.EndedUtc.HasValue &&
        item.JobId is "official-share-sync" or "market-activity-sync" or "official-calendar-sync" or "equity-disclosure-sync";

    public static ShellStatusSnapshot Unavailable() => new(
        "服务不可用","Error","无法确认 Windows Service 实时状态；可能是未启动、权限或管道连接问题。",
        "运行 -- · 等待 --","无实时 Worker 调度快照。",
        "最近同步：未确认","连接中断，不能继续沿用上次同步快照。",
        "运行异常：未确认","未读取到新健康采样；旧正常状态已撤回。");

    /// <summary>纯快照映射便于不启动服务的 WPF/状态回归；失败时不能把缺字段默认为0。</summary>
    public static ShellStatusSnapshot Describe(BackgroundResponse response,IReadOnlyList<ExecutionRecord>? history,int inspected,bool historyVerified,
        DataCleanupTask? cleanupTask=null,bool cleanupKnown=true,BackgroundTaskActivity? activity=null)
    {
        if(response.Code!="Ok" || response.Health is not { } health)return Unavailable();
        var state=health.State;
        var (label,tone)=state switch
        {
            "Running" => ("服务正常","Success"),
            "Degraded" => ("部分服务异常","Warning"),
            "Maintenance" => ("后台维护中","Warning"),
            "StorageFault" => ("后台存储故障","Error"),
            "PluginUnresponsive" or "UnresponsivePlugin" => ("插件无响应","Error"),
            _ => ("服务状态异常","Error")
        };
        var sample=health.ObservedUtc.ToString("MM-dd HH:mm:ss");
        var scheduling=health.Scheduling;
        int? running=activity?.Running??scheduling?.Running;
        int? waiting=activity is null?null:activity.Waiting+activity.Pending;
        var taskDetail=activity is null?"尚未取得可去重的当前任务快照，等待数量未确认；不能将健康采样中的排队和手动请求数量直接相加。":
            $"当前任务：运行 {activity.Running}，等待 {waiting}（排队 {activity.Waiting}、尚未入队请求 {activity.Pending}，同一快照已去重）。采样 {activity.ObservedAt:MM-dd HH:mm:ss}（北京时间）。";
        if(!cleanupKnown)
        {
            running=null;waiting=null;
            taskDetail+=" 清理任务未取得最新状态，以上数量仅含插件任务。";
        }
        else if(CleanupTaskPresentation.Active(cleanupTask))
        {
            var queued=cleanupTask!.State=="Queued";
            if(queued)waiting+=1;else running+=1;
            taskDetail+=" 数量已另计1项后台资料清理（独立于插件执行名额）："+CleanupTaskPresentation.Detail(cleanupTask)+
                $"；清理采样 {cleanupTask.UpdatedAt:MM-dd HH:mm:ss}（北京时间）。";
        }
        var tasks=$"运行 {running?.ToString()??"--"} · 等待 {waiting?.ToString()??"--"}";
        var hasSources=health.Sources is not null;
        var cooling=health.Sources?.Count(x=>x.State=="Cooldown")??0;
        var hasPlugins=response.Plugins is not null;
        var failedPlugins=response.Plugins?.Count(x=>x.State=="Failed")??0;
        var counted=cooling+failedPlugins;
        var attention=state is not ("Running" or "Degraded" or "Maintenance")
            ? "服务故障 · 查看":
            state=="Maintenance" ? "维护进行中":
            !hasSources || !hasPlugins ? "运行异常：未确认":
            counted>0 ? $"运行异常 {counted} 项":
            state=="Degraded" ? "服务异常 · 查看" : "运行异常 0";
        var attentionDetail=$"来源冷却 {cooling} · 插件失败 {failedPlugins}。仅代表服务可观察的当前异常，不含税务/ETF数据缺口与审核待办。";
        if(!hasSources || !hasPlugins)attentionDetail="当前服务未返回完整来源/插件健康快照；运行异常数不能按零报告。";
        var recent=historyVerified
            ?history?.Where(IsSuccessfulSync).OrderByDescending(x=>x.EndedUtc).FirstOrDefault():null;
        var sync=recent is null
            ?historyVerified?(inspected==0?"最近同步：暂无记录":$"近{inspected}条未见成功同步"):"最近同步：未确认"
            :$"最近同步 {recent.EndedUtc!.Value:MM-dd HH:mm}";
        var syncDetail=recent is null
            ?historyVerified?$"已核查国家队插件最近 {inspected} 条执行记录，尚未发现已成功结束的指定同步任务；不代表更早不存在成功记录。":
                "国家队插件执行历史未核实，不能据服务健康状态推断采集成功。"
            :$"国家队后台同步任务：{recent.JobId}，执行完成时间 {recent.EndedUtc!.Value:yyyy-MM-dd HH:mm:ss}（北京时间）；不代表已取得当天交易数据。";
        return new(label,tone,$"Worker 状态 {state} · 采样 {sample}（北京时间）",
            tasks,taskDetail,sync,syncDetail,attention,attentionDetail);
    }
}

public sealed record ShellStatusSnapshot(
    string HealthLabel,string HealthTone,string HealthDetail,
    string TaskLabel,string TaskDetail,
    string LastSyncLabel,string LastSyncDetail,
    string AttentionLabel,string AttentionDetail);
