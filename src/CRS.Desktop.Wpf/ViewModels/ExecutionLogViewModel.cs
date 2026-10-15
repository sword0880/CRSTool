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
    private static readonly TimeZoneInfo DisplayZone=TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    public CancellationToken Lifetime=>token;
    public string ExecutionId=>record.ExecutionId;
    public string TaskLabel=>record.PluginId+" / "+record.JobId;
    [ObservableProperty] private string status="显示打开时的记录，正在查询服务。";
    [ObservableProperty] private string logText="";

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
        var lines=new List<string>
        {
            $"[{Time(record.ScheduledUtc)}] 计划触发",
            $"[{Time(record.StartedUtc)}] 开始执行 · 第 {record.Attempt} 次尝试",
            "",
            "当前结果："+ResultLabel(record.Result)
        };
        if(record.EndedUtc is { } ended)lines.Add($"[{Time(ended)}] 记录结束 · {ResultLabel(record.Result)}");
        if(!string.IsNullOrWhiteSpace(record.Checkpoint))lines.Add("执行摘要："+Describe(record.Checkpoint));
        else lines.Add(record.Result=="Running"?"本次执行尚未保存结束摘要，可点击刷新查看。":"本条历史未保存详细执行摘要。");
        lines.Add("");lines.Add("仅显示后台服务已保存的执行信息；未记录的逐步调试日志无法补查。");
        LogText=string.Join(Environment.NewLine,lines);
    }
    private static string ResultLabel(string value)=>value switch
    {
        "Running"=>"进行中", "Succeeded"=>"成功", "Failed"=>"失败", "Cancelled"=>"已取消", "Interrupted"=>"执行中断",
        "NotPublished"=>"等待来源发布", "RetryDeferred"=>"延后重试", "AccessBlocked"=>"来源访问受限", "SourceSchemaChanged"=>"来源格式变化", _=>value
    };
    private static string Describe(string value)
    {
        var parts=value.Split(';');
        if(parts.Length==3 && parts[0].StartsWith("days=",StringComparison.Ordinal) && parts[1].StartsWith("sourceIssues=",StringComparison.Ordinal) && parts[2].StartsWith("rows=",StringComparison.Ordinal)
            && int.TryParse(parts[0][5..],out var days) && days>=0 && int.TryParse(parts[1][13..],out var issues) && issues>=0 && int.TryParse(parts[2][5..],out var rows) && rows>=0)
            return $"查询 {days} 个自然日，收到 {rows} 条记录，来源问题 {issues} 项。";
        return value.StartsWith("error=",StringComparison.Ordinal)?"错误分类："+value[6..]:value;
    }
    public void Dispose(){lifetime.Cancel();lifetime.Dispose();}
}
