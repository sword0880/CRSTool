using CRS.Maintenance;
namespace CRS.Application.Abstractions;

/// <summary>设置只提交固定选项及服务签发的预览身份，不决定文件用途或自行删除后台资料。</summary>
public interface IDataCleanupGateway
{
    bool CanExecute{get;}
    Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token);
    Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token);
    Task<DataCleanupStatus> StatusAsync(Guid operationId,IProgress<DataCleanupStatus>? progress,CancellationToken token);
}
