using System.IO;
using System.Text.Json;
using CRS.Maintenance;
using CRS.Worker.Ipc;
namespace CRS.DesktopClient.Services;

public sealed partial class WorkerDataCleanupGateway
{
    public Task<DataCleanupTaskOverview> SubmitAsync(Guid requestId,CancellationToken token)
        =>TaskRequestAsync(new(WorkerProtocol.Version,requestId,"SubmitDataCleanupTask"),token);
    public Task<DataCleanupTaskOverview> LatestAsync(CancellationToken token)
        =>TaskRequestAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupTasks"),token);
    public Task<DataCleanupTaskOverview> AcknowledgeAsync(Guid taskId,CancellationToken token)
        =>TaskRequestAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"AcknowledgeDataCleanupTask",RelatedRequestId:taskId),token);
    private async Task<DataCleanupTaskOverview> TaskRequestAsync(BackgroundRequest request,CancellationToken token)
    {
        var reply=await send(request,token).ConfigureAwait(false);
        if(reply.Code!="Ok"||reply.Payload is null)throw new InvalidOperationException(reply.Code switch
        {
            "UnknownCommand" or "UnsupportedCommand"=>"当前后台不支持清理任务，请更新配套后台服务。",
            "Forbidden"=>"提交清理任务需要管理员权限。",
            "Busy"=>"后台已有手动扫描或维护操作，请稍后提交。",
            _=>"未取得后台清理任务记录（"+reply.Code+"），可稍后重试核对。"
        });
        var result=JsonSerializer.Deserialize<DataCleanupTaskOverview>(reply.Payload)??throw new InvalidDataException("后台清理任务记录缺失。");
        foreach(var task in new[]{result.Current,result.LatestResult,result.Notification}.OfType<DataCleanupTask>())
            if(task.Id==Guid.Empty||task.State is not ("Queued" or "Scanning" or "Running" or "Completed" or "Interrupted" or "Unavailable")
                ||task.DeletedFiles<0||task.DeletedBytes<0||task.SubmittedAt==default||task.UpdatedAt==default)
                throw new InvalidDataException("后台清理任务记录无效。");
        return result;
    }
}
