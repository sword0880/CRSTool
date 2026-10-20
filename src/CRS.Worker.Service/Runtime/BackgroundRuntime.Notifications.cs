using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    /// <summary>邮件设置必须管理员授权；慢SMTP不占全局调度锁，敏感参数不进入持久任务命令。</summary>
    private async Task<BackgroundResponse> HandleEmailAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
    {
        BackgroundResponse Reply(string code,string? payload=null)=>new(WorkerProtocol.Version,request.RequestId,code,Payload:payload);
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(token);
        try
        {
            if(!caller.CanManage)return Reply("Forbidden");
            if(maintenance||fault is not null)return Reply("Maintenance");
            if(notifications is null)return Reply("NotificationChannelUnavailable");
            activeQueries.Add(completed);
        }
        finally{gate.Release();}
        var webhook=request.Command.Contains("Webhook",StringComparison.Ordinal);
        var result=webhook?"WebhookSettingsUnavailable":"EmailSettingsUnavailable";
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,queryStopping.Token);
        try
        {
            object value=request.Command switch
            {
                "GetEmailSettings"=>notifications.Read(),
                "SaveEmailSettings"=>notifications.Save(JsonSerializer.Deserialize<WorkerEmailUpdate>(request.Payload??"")??throw new InvalidDataException("邮件参数为空。")),
                "GetWebhookSettings"=>notifications.ReadWebhook(request.Payload??""),
                "SaveWebhookSettings"=>notifications.SaveWebhook(JsonSerializer.Deserialize<WorkerWebhookUpdate>(request.Payload??"")??throw new InvalidDataException("通知参数为空。")),
                "TestWebhookSettings"=>await notifications.TestWebhookAsync(request.Payload??"",request.RequestId,cancellation.Token),
                _=>await notifications.TestAsync(request.RequestId,cancellation.Token)
            };
            result="Ok";return Reply(result,JsonSerializer.Serialize(value));
        }
        catch(Exception error) when(error is ArgumentException or InvalidDataException or JsonException){result=webhook?"InvalidWebhookSettings":"InvalidEmailSettings";return Reply(result);}
        catch(Exception error) when(error is CryptographicException or IOException or UnauthorizedAccessException){return Reply(result);}
        finally
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                // 仅记录动作与结果分类，不记录口令、Payload或第三方异常正文。
                if(request.Command is not ("GetEmailSettings" or "GetWebhookSettings"))repository.Audit(caller.Sid,request.RequestId,request.Command,result);
            }
            finally{activeQueries.Remove(completed);completed.TrySetResult();gate.Release();}
        }
    }
}
