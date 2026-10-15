namespace CRS.Worker.Abstractions;

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
    string TimeZoneId, bool EnabledByDefault, bool RunOnStartup = false,bool ManualOnly=false);

/// <summary>可预期的数据源结果使用分类返回，服务据此执行退避。</summary>
public sealed record BackgroundJobResult(string Status, string? Checkpoint = null, TimeSpan? RetryAfter = null)
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

/// <summary>持久命令与某次执行分离；重试请求沿用原标识，不能重新创建业务工作。</summary>
public sealed record CommandReceipt(Guid RequestId, string State, string Code, string? ExecutionId);

/// <summary>插件故障按实例暴露，不以整个服务重启掩盖单个插件加载失败。</summary>
public sealed record BackgroundPluginStatus(string PluginId, string State, string? ErrorCode = null);

/// <summary>需要存储的可信插件由宿主注入私有目录和派生密钥，不传入税务认证或宿主根容器。</summary>
public interface IContextualBackgroundPluginEntry : IBackgroundPluginEntry
{
    IBackgroundPluginSession CreateSession(IBackgroundHostContext context);
}
public interface IBackgroundHostContext
{
    string PluginId { get; }
    string DataDirectory { get; }
    byte[] OpenDataKey();
}
/// <summary>写参数由服务持久命令恢复；普通定时执行使用空参数。</summary>
public interface IParameterizedBackgroundSession
{
    Task<BackgroundJobResult> ExecuteAsync(string jobId,string? payload,CancellationToken token,Guid? commandId=null);
}
/// <summary>只读业务查询返回有界 JSON，不接受文件路径或执行任意方法。</summary>
public interface IBackgroundQuerySession
{
    Task<string> QueryAsync(string payload,CancellationToken token);
}
