namespace CRS.Plugin.Abstractions;

/// <summary>可选计划任务能力；Quartz实现属于主程序，不向插件泄漏调度器或容器。</summary>
public interface IPluginScheduleContext { IPluginScheduleGateway Schedules {get;} }
public interface IPluginScheduleGateway
{
    Task RegisterAsync(string taskId,string cron,string timeZoneId,Func<CancellationToken,Task> work,CancellationToken cancellationToken,TimeSpan? startupDelay=null,string? displayName=null);
}
/// <summary>部分来源失败但已有事实保留时，调度记录为不完整，异常只携带固定类别。</summary>
public sealed class PluginTaskIncompleteException():Exception("任务部分完成，请查看插件来源状态。");
