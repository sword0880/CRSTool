using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
namespace CRS.Worker.Service;

/// <summary>调度器仅经仓储操作权威状态，便于对真实持久化边界进行故障注入。</summary>
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
    (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? cursor, int pageSize,string? plugin=null);
    void Audit(string sid, Guid request, string command, string result);
    CommandReceipt? FindCommand(Guid request, string sid);
    BackgroundRequest ReadCommand(Guid request);
    CommandReceipt AcceptCommand(BackgroundRequest request, string sid);
    void CompleteCommand(Guid request, string code);
    List<(Guid RequestId, string PluginId, string JobId)> PendingRuns();
    void CancelPending(string plugin, string job);
}
