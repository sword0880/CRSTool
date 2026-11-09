using System.IO;
using System.Text.Json;
using CRS.Maintenance;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
namespace CRS.DesktopClient.Services;

public sealed partial class WorkerDataCleanupGateway
{
    private const string TaskOverviewCommand = "GetDataCleanupTasks";

    public Task<DataCleanupTaskOverview> SubmitAsync(Guid requestId,CancellationToken token)
        =>TaskRequestAsync(new(WorkerProtocol.Version,requestId,"SubmitDataCleanupTask"),token);
    public Task<DataCleanupTaskOverview> LatestAsync(CancellationToken token)
        =>TaskRequestAsync(new(WorkerProtocol.Version,Guid.NewGuid(),TaskOverviewCommand),token);
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
        return ParseTaskOverview(reply.Payload);
    }

    internal static async Task<(string Code, IReadOnlyList<ExecutionRecord>? History, bool LatestOnly)> ReadHistoryAsync(
        BackgroundPipeClient client, CancellationToken token)
    {
        var reply = await client.QueryAsync(DataCleanupTask.HistoryCommand, token).ConfigureAwait(false);
        if (reply.Code is not ("UnknownCommand" or "UnsupportedCommand")) return (reply.Code, reply.History, false);

        var legacy = await client.QueryAsync(TaskOverviewCommand, token).ConfigureAwait(false);
        if (legacy.Code != "Ok") return (legacy.Code, null, false);
        var task = ParseTaskOverview(legacy.Payload).LatestResult;
        if (task is null) return ("Ok", [], true);
        if (task.FinishedAt is null || task.State is not ("Completed" or "Interrupted" or "Unavailable"))
            throw new InvalidDataException("后台返回的最近清理结果尚未结束。");
        return ("Ok", [ExecutionRecord.FromDataCleanupTask(task)], true);
    }

    private static DataCleanupTaskOverview ParseTaskOverview(string? payload)
    {
        var result=payload is null?null:JsonSerializer.Deserialize<DataCleanupTaskOverview>(payload);
        if(result is null)throw new InvalidDataException("后台清理任务记录缺失。");
        if(result.AutomaticPlan is {} plan&&(plan.State is not ("NotStarted" or "Running" or "Waiting" or "Due" or "Paused" or "Unavailable")
            ||plan.StartupDelaySeconds<0||plan.CheckIntervalSeconds<1||plan.SuccessIntervalSeconds<1||plan.RetryIntervalSeconds<1
            ||plan.LogRetentionDays<14||plan.HistoricalBackupsToKeep<1))throw new InvalidDataException("后台维护计划摘要无效。");
        foreach(var task in new[]{result.Current,result.LatestResult,result.Notification}.OfType<DataCleanupTask>())
            if(task.Id==Guid.Empty||task.State is not ("Queued" or "Scanning" or "Running" or "Completed" or "Interrupted" or "Unavailable")
                ||task.DeletedFiles<0||task.DeletedBytes<0||task.SubmittedAt==default||task.UpdatedAt==default)
                throw new InvalidDataException("后台清理任务记录无效。");
        return result;
    }
}
