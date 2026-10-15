namespace CRS.Background.Abstractions;

/// <summary>后台插件只声明任务和处理器；调度器与持久计划由服务唯一持有。</summary>
public interface IBackgroundPluginEntry
{
    IBackgroundPluginSession CreateSession();
}

/// <summary>无界面会话，不持有桌面登录态、税务密钥或根服务容器。</summary>
public interface IBackgroundPluginSession : IAsyncDisposable
{
    IReadOnlyList<BackgroundJobDescriptor> DescribeJobs();
    ValueTask StartAsync(CancellationToken cancellationToken);
    Task<BackgroundJobResult> ExecuteAsync(string jobId, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>任务名称是稳定业务标识；禁止将实现类型或委托序列化到数据库。</summary>
public sealed record BackgroundJobDescriptor(string JobId, string Name, string DefaultCron,
    string TimeZoneId, bool EnabledByDefault);

/// <summary>可预期的数据源结果使用分类返回，服务据此执行退避。</summary>
public sealed record BackgroundJobResult(string Status, string? Checkpoint = null)
{
    public static BackgroundJobResult Success(string? checkpoint = null) => new("Succeeded", checkpoint);
}

/// <summary>服务和管理端共用的只读计划快照，修订号用于并发编辑检测。</summary>
public sealed record TaskDefinition(string PluginId, string JobId, string Name, string Cron,
    string TimeZoneId, bool Enabled, long Revision, bool NeedsReconcile,
    DateTimeOffset? NotBefore = null, int Failures = 0);

/// <summary>历史只包含结果分类，不持久化可能含凭据的异常消息或堆栈。</summary>
public sealed record ExecutionRecord(string ExecutionId, string PluginId, string JobId,
    DateTimeOffset ScheduledUtc, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    string Result, string? Checkpoint, int Attempt);
