using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.ViewModels;

/// <summary>只查询选中执行的持久记录，不读取服务文件，也不把当前任务状态冒充历史日志。</summary>
public partial class ExecutionLogViewModel : ObservableObject, IDisposable
{
    private readonly BackgroundPipeClient client;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken token;
    private ExecutionRecord record;
    private bool disposed;
    private static readonly TimeZoneInfo DisplayZone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    public CancellationToken Lifetime=>token;
    public string ExecutionId=>record.ExecutionId;
    public string TaskLabel=>record.PluginId+" / "+record.JobId;
    public bool IsRunning=>record.Result=="Running";
    public string StateLabel=>ResultLabel(record.Result);
    public string StateBackground=>record.Result=="Succeeded"?"#ECFDF5":IsRunning?"#EFF6FF":"#FFF7ED";
    public string StateForeground=>record.Result=="Succeeded"?"#15803D":IsRunning?"#2563EB":"#B45309";
    [ObservableProperty] private string progressLabel="";
    [ObservableProperty] private string status="显示打开时的记录，正在查询服务。";
    [ObservableProperty] private string logText="";
    [ObservableProperty] private string search="";
    [ObservableProperty] private bool onlyIssues;
    public string FullLogText {get;private set;}="";
    private readonly List<(string Text,bool Issue)> displayLines=[];
    partial void OnSearchChanged(string value)=>ApplyFilter();
    partial void OnOnlyIssuesChanged(bool value)=>ApplyFilter();
    private void ApplyFilter()
    {
        var matches=displayLines.Where(x=>(!OnlyIssues||x.Issue) && (string.IsNullOrWhiteSpace(Search)||x.Text.Contains(Search.Trim(),StringComparison.OrdinalIgnoreCase))).Select(x=>x.Text).ToArray();
        LogText=matches.Length==0?"没有符合条件的日志。":string.Join(Environment.NewLine,matches);
    }

    public ExecutionLogViewModel(ExecutionRecord initial,BackgroundPipeClient client,CancellationToken parentLifetime)
    {
        record=initial;this.client=client;lifetime=CancellationTokenSource.CreateLinkedTokenSource(parentLifetime);token=lifetime.Token;Render();
    }
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var response=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:ExecutionId),Lifetime);
            Lifetime.ThrowIfCancellationRequested();
            if(response.Code=="Ok" && response.Execution is { } latest && latest.ExecutionId==ExecutionId)
            {
                record=latest;Render();Status="服务采样："+Time(DateTimeOffset.UtcNow)+"（北京时间）";
            }
            else Status=response.Code switch
            {
                "UnknownExecution"=>"服务已清理这条记录，以下保留打开时的快照。",
                "UnknownCommand"=>"后台服务版本尚不支持日志查询，请更新服务；以下为打开时的快照。",
                "RateLimited"=>"查询频繁，请稍后刷新；以下为上次快照。",
                _=>"日志暂不可用；以下为上次快照。"
            };
        }
        catch(OperationCanceledException) when(Lifetime.IsCancellationRequested) { }
        catch(Exception) { if(!Lifetime.IsCancellationRequested)Status="服务不可用或响应超时；以下为上次快照。"; }
    }
    private static string Time(DateTimeOffset value)=>TimeZoneInfo.ConvertTime(value,DisplayZone).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
    /// <summary>时间线仅由真实持久字段组成；未保存的逐步调试过程不能凭空补写。</summary>
    private void Render()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StateLabel));OnPropertyChanged(nameof(StateBackground));OnPropertyChanged(nameof(StateForeground));
        var lines=new List<string>
        {
            $"[{Time(record.ScheduledUtc)}] 计划触发",
            $"[{Time(record.StartedUtc)}] 开始执行 · 第 {record.Attempt} 次尝试",
            "",
            "当前结果："+ResultLabel(record.Result)
        };
        if(record.Progress is {Count:>0} progress)
        {
            if(progress[0].Sequence>1)lines.Add($"仅返回最近{progress.Count}条阶段；更早阶段未在本次响应中返回。");
            foreach(var item in progress)lines.Add($"[{Time(item.Utc)}] #{item.Sequence} · "+DescribeProgress(item.Detail));
            var last=progress[^1];var seconds=Math.Max(0,(int)(DateTimeOffset.UtcNow-last.Utc).TotalSeconds);
            ProgressLabel=IsRunning?$"最近阶段：{Time(last.Utc)} · {(seconds>=60?$"{seconds/60}分钟前":$"{seconds}秒前")} · {DescribeProgress(last.Detail)}":$"最后阶段：{Time(last.Utc)} · {DescribeProgress(last.Detail)}";
        }
        else ProgressLabel=IsRunning?"尚无阶段记录；旧服务仅提供结束摘要。":"此历史没有阶段记录。";
        if(record.EndedUtc is { } ended)lines.Add($"[{Time(ended)}] 记录结束 · {ResultLabel(record.Result)}");
        if(!string.IsNullOrWhiteSpace(record.Checkpoint))lines.Add("执行摘要："+Describe(record.Checkpoint));
        else lines.Add(record.Result=="Running"?"本次执行尚未保存结束摘要，可点击刷新查看。":"本条历史未保存详细执行摘要。");
        lines.Add("");lines.Add("仅显示后台服务已保存的执行信息；未记录的逐步调试日志无法补查。");
        FullLogText=string.Join(Environment.NewLine,lines);
        displayLines.Clear();
        var issueSequences=record.Progress?.Where(x=>x.Detail.Stage is "SourceFailed" or "RequestTimedOut" or "RequestFailed" or "RateWait" or "Cooldown").Select(x=>x.Sequence).ToHashSet() ?? [];
        foreach(var line in lines)
        {
            var marker=line.IndexOf("] #",StringComparison.Ordinal);
            var end=marker<0?-1:line.IndexOf(" · ",marker,StringComparison.Ordinal);
            var issue=marker>=0 && end>marker && long.TryParse(line[(marker+3)..end],out var sequence) && issueSequences.Contains(sequence);
            displayLines.Add((line,issue || (line.StartsWith("当前结果：",StringComparison.Ordinal) && record.Result is not ("Running" or "Succeeded"))));
        }
        ApplyFilter();
    }
    /// <summary>按结构化白名单翻译进度，始终区分来源行数与真实提交数量。</summary>
    private static string DescribeProgress(BackgroundProgress value)
    {
        var scope=$"{value.Date:yyyy-MM-dd} {value.Market}".Trim();
        var counts=$"收到{value.Received}条；新增已接受{value.Added}条，重复跳过{value.Duplicates}条，新增待审核{value.Pending}条";
        var stage=value.Stage switch
        {
            "RangeStarted"=>"开始区间采集", "DayStarted"=>"开始查询日期", "SourceStarted"=>"开始读取官方来源",
            "SourceParsed"=>$"来源解析完成：{value.Received}条（含非监控证券，写入时过滤）", "SourceFailed"=>"来源未完成，未将失败当作零份额",
            "RequestStarted"=>"发起HTTPS请求（响应头和正文共用20秒期限）", "ResponseReceived"=>$"收到HTTP {value.HttpStatus}响应头，继续读取和校验正文",
            "RequestTimedOut"=>"请求超过20秒期限，已登记重试退避", "RequestFailed"=>"网络连接失败，已登记重试退避", "RateWait"=>"按来源间隔等待", "Cooldown"=>"来源冷却，停止本次网络请求",
            "DayCommitted"=>"当日写入完成 · "+counts, "RangeCompleted"=>"区间查询结束 · "+counts, _=>"未知阶段"
        };
        return scope+" · "+stage+(value.Until is { } until?$"；最早继续时间 {Time(until)}（北京时间）":"");
    }
    private static string ResultLabel(string value)=>value switch
    {
        "Running"=>"进行中", "Succeeded"=>"成功", "Rejected"=>"请求未接受", "Failed"=>"失败", "Cancelled"=>"已取消", "Interrupted"=>"执行中断",
        "NotPublished"=>"等待来源发布", "RetryDeferred"=>"延后重试", "AccessBlocked"=>"来源访问受限", "SourceSchemaChanged"=>"来源格式变化", _=>value
    };
    private static string Describe(string value)
    {
        if(value=="configuration=InvalidOrStale")return "ETF配置输入不符合要求或已被其它窗口修改，请刷新目录后重试。";
        if(value=="monitoring=NoActiveEtfs")return "当前没有启用监控的ETF，本次没有发起网络采集。";
        var parts=value.Split(';');
        if(parts.Length==3 && parts[0].StartsWith("days=",StringComparison.Ordinal) && parts[1].StartsWith("sourceIssues=",StringComparison.Ordinal) && parts[2].StartsWith("rows=",StringComparison.Ordinal)
            && int.TryParse(parts[0][5..],out var days) && days>=0 && int.TryParse(parts[1][13..],out var issues) && issues>=0 && int.TryParse(parts[2][5..],out var rows) && rows>=0)
            return $"查询 {days} 个自然日，收到 {rows} 条记录，来源问题 {issues} 项。";
        return value.StartsWith("error=",StringComparison.Ordinal)?"错误分类："+value[6..]:value;
    }
    public void Dispose(){if(disposed)return;disposed=true;lifetime.Cancel();lifetime.Dispose();}
}
