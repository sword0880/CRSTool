using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using System.Windows.Threading;

namespace CRS.DesktopClient.ViewModels;

/// <summary>
/// 只读桌面壳运行状态：不承接税务敏感数据、管理命令或第二套后台调度。
/// 采样来自独立 Worker，连接失败立即撤回旧绿色状态。
/// </summary>
public partial class ShellStatusBarViewModel : ObservableObject
{
    private readonly BackgroundPipeClient client;
    private DispatcherTimer? timer;
    private CancellationTokenSource? polling;
    private bool refreshing;

    public ShellStatusBarViewModel(BackgroundPipeClient? client = null) => this.client = client ?? new();

    [ObservableProperty] private string healthLabel = "后台状态待确认";
    [ObservableProperty] private string healthTone = "Warning";
    [ObservableProperty] private string healthDetail = "尚未取得独立 Windows Service 的健康采样。";
    [ObservableProperty] private string taskLabel = "运行 -- · 排队 --";
    [ObservableProperty] private string taskDetail = "尚未取得真实调度状态。";
    [ObservableProperty] private string lastSyncLabel = "最近同步：未确认";
    [ObservableProperty] private string lastSyncDetail = "仅统计国家队后台插件的成功同步，不推断数据最新交易日期。";
    [ObservableProperty] private string attentionLabel = "运行异常：未确认";
    [ObservableProperty] private string attentionDetail = "仅统计 Worker 的插件故障及官方来源冷却，不代表业务审核待办总数。";

    /// <summary>窗口展示期间每45秒轻量轮询；实例化页面或预览时不主动建立管道连接。</summary>
    public void Start(CancellationToken lifetime)
    {
        if(timer is not null || lifetime.IsCancellationRequested)return;
        polling=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(45)};
        timer.Tick+=OnTick;
        timer.Start();
        _=RefreshAsync();
    }

    public void Stop()
    {
        if(timer is not null){timer.Stop();timer.Tick-=OnTick;timer=null;}
        polling?.Cancel();polling?.Dispose();polling=null;
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
            Update(Describe(health,null,0,false));
            if(health.Code!="Ok" || health.Health?.State is not ("Running" or "Degraded" or "Maintenance"))return;

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
            Update(Describe(health,rows,rows.Count,true));
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception)
        {
            // 连接断开或任意读取异常时不得保留旧的“服务正常/零异常”。
            if(!token.IsCancellationRequested)Update(Unavailable());
        }
        finally{refreshing=false;}
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
        "运行 -- · 排队 --","无实时 Worker 调度快照。",
        "最近同步：未确认","连接中断，不能继续沿用上次同步快照。",
        "运行异常：未确认","未读取到新健康采样；旧正常状态已撤回。");

    /// <summary>纯快照映射便于不启动服务的 WPF/状态回归；失败时不能把缺字段默认为0。</summary>
    public static ShellStatusSnapshot Describe(BackgroundResponse response,IReadOnlyList<ExecutionRecord>? history,int inspected,bool historyVerified)
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
        var sample=health.ObservedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
        var scheduling=health.Scheduling;
        var tasks=scheduling is null?"运行 -- · 排队 --":$"运行 {scheduling.Running} · 排队 {scheduling.Waiting}";
        var taskDetail=scheduling is null?"后台未提供调度快照。":
            $"Worker 调度快照：运行 {scheduling.Running}，等待 {scheduling.Waiting}，另有待执行的持久手动命令 {scheduling.PendingManualCommands}。";
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
            :$"最近同步 {recent.EndedUtc!.Value.ToLocalTime():MM-dd HH:mm}";
        var syncDetail=recent is null
            ?historyVerified?$"已核查国家队插件最近 {inspected} 条执行记录，尚未发现已成功结束的指定同步任务；不代表更早不存在成功记录。":
                "国家队插件执行历史未核实，不能据服务健康状态推断采集成功。"
            :$"国家队后台同步任务：{recent.JobId}，执行完成时间 {recent.EndedUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本地时区）；不代表已取得当天交易数据。";
        return new(label,tone,$"Worker 状态 {state} · 采样 {sample}（本地时区）",
            tasks,taskDetail,sync,syncDetail,attention,attentionDetail);
    }
}

public sealed record ShellStatusSnapshot(
    string HealthLabel,string HealthTone,string HealthDetail,
    string TaskLabel,string TaskDetail,
    string LastSyncLabel,string LastSyncDetail,
    string AttentionLabel,string AttentionDetail);
