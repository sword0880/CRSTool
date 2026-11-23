using CRS.Maintenance;
namespace CRS.Application.Abstractions;

/// <summary>前台只提交固定规则的完整清理任务，读取摘要并确认通知；不维持长请求。</summary>
public interface IDataCleanupTaskGateway
{
    bool CanExecute{get;}
    Task<DataCleanupTaskOverview> SubmitAsync(Guid requestId,CancellationToken token);
    Task<DataCleanupTaskOverview> LatestAsync(CancellationToken token);
    Task<DataCleanupTaskOverview> AcknowledgeAsync(Guid taskId,CancellationToken token);
}
