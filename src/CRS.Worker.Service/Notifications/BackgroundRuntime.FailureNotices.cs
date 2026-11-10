using CRS.Worker.Abstractions;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private Task? failureNotifications;

    private void QueueFailureNotifications()
    {
        if (notifications is null || maintenance || failureNotifications is {IsCompleted:false}) return;
        failureNotifications = Task.Run(() => DeliverFailureNoticesAsync(notifications,queryStopping.Token),CancellationToken.None);
    }

    private async Task DeliverFailureNoticesAsync(IBackgroundNotificationContext channels,CancellationToken token)
    {
        try
        {
            for (var batch=0;batch<10;batch++)
            {
                token.ThrowIfCancellationRequested();
                if (repository.NextFailureNotice() is not { } notice) return;
                var destinations=await channels.NotificationChannelsAsync(token).ConfigureAwait(false);
                var status=notice.Result switch {"AccessBlocked"=>"来源访问受限","SourceSchemaChanged"=>"来源格式变化",_=>"执行失败"};
                var body=$"后台任务 {notice.PluginId}/{notice.JobId} 已重试 {TaskRetryPolicy.MaximumRetries} 次，最终结果为{status}。\n"
                    +$"北京时间 {notice.At:yyyy-MM-dd HH:mm:ss}\n请在任务中心查看执行记录。";
                var message=new BackgroundNotification("job-failure."+notice.Id,"CRS 后台任务重试后仍失败",body,"TaskFailure");
                foreach(var destination in destinations.Where(x=>x.Enabled&&x.Configured&&!notice.Attempted.Contains(x.Id)))
                {
                    token.ThrowIfCancellationRequested();
                    // 发出前持久标记，避免进程退出或提供商响应中断时重复发送结果未确认的通知。
                    repository.MarkFailureChannel(notice,destination.Id);
                    notice=notice with {Attempted=[..notice.Attempted,destination.Id]};
                    var result=await channels.SendNotificationAsync(destination.Id,"default",message,token).ConfigureAwait(false);
                    diagnostics?.Invoke("TaskFailureNotification",destination.Id,result.State);
                }
                repository.CompleteFailureNotice(notice.Id);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception error) when(error is not OutOfMemoryException)
        {diagnostics?.Invoke("TaskFailureNotificationUnavailable","service",error.GetType().Name);}
    }
}
