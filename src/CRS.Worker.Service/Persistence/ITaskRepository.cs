using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
namespace CRS.Worker.Service;

/// <summary>
/// 调度器仅经仓储操作权威状态；接口不暴露数据库连接、事务、SQL或提供方类型。
/// Save（含禁用时取消待执行命令）、Begin、End及关联命令状态必须各自原子提交。
/// 持久化IO故障以IOException上报，权限/损坏分别保留对应BCL异常；不能返回空状态掩盖错误。
/// 业务冲突仍为InvalidOperationException，调用方不能把它当作存储不可用。
/// HistoryPage的游标不透明，由同一适配器解析，调用方只原样传回。
/// </summary>
public interface ITaskRepository
{
    void RegisterDefault(string plugin, BackgroundJobDescriptor descriptor);
    List<TaskDefinition> Plans();
    void Save(TaskDefinition plan, long expectedRevision, Guid? requestId = null);
    void Reconciled(TaskDefinition plan);
    string Begin(TaskDefinition plan, DateTimeOffset scheduled, Guid? requestId = null);
    void End(string id, TaskDefinition plan, BackgroundJobResult result, Guid? requestId = null);
    List<ExecutionRecord> History();
    ExecutionRecord? FindExecution(string id);
    void AppendProgress(string id,BackgroundProgress progress);
    (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? cursor, int pageSize,string? plugin=null,string? job=null,string? resultFilter=null,DateOnly? date=null);
    void Audit(string sid, Guid request, string command, string result);
    CommandReceipt? FindCommand(Guid request, string sid);
    BackgroundRequest ReadCommand(Guid request);
    /// <summary>仅供后台调度读取已受理命令的状态，不能作为跨调用者公开查询。</summary>
    CommandReceipt? ReadCommandState(Guid request);
    CommandReceipt AcceptCommand(BackgroundRequest request, string sid);
    void CompleteCommand(Guid request, string code);
    List<(Guid RequestId, string PluginId, string JobId)> PendingRuns();
    /// <summary>取消Pending，并为Running持久记录取消意图；不将尚未确认的业务结果宣称为已取消。</summary>
    void CancelPending(string plugin, string job);
}
