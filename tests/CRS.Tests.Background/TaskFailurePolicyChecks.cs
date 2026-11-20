using System.Security.Cryptography;
using CRS.Notifications;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class TaskFailurePolicyChecks
{
    internal static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.TaskFailures",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"scheduler.db");
        var key=RandomNumberGenerator.GetBytes(32);
        var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","test","job",Payload:"{} ");
        try
        {
            for (var attempt=1;attempt<=4;attempt++)
            {
                using var repository=new EncryptedTaskRepository(path,key);
                if (attempt==1)
                {
                    repository.RegisterDefault("test",new("job","任务","0 0 0 * * ?","China Standard Time",true));
                    repository.AcceptCommand(request,"admin");
                }
                var plan=repository.Plans().Single();
                var execution=repository.Begin(plan,DateTimeOffset.UtcNow,request.RequestId);
                repository.End(execution,plan,new("Failed"),request.RequestId);
                var latest=repository.Plans().Single();
                Check(latest.LastStartedAt is not null&&latest.LastResult=="Failed"&&repository.FindExecution(execution)?.Attempt==attempt,
                    "持久记录每次执行的时间、结果和尝试编号");
                var receipt=repository.FindCommand(request.RequestId,"admin");
                if(attempt<=3)
                    Check(latest is {NotBefore:not null}&&latest.Failures==attempt&&receipt is {State:"Pending",Code:"RetryDeferred"}
                        &&repository.ReadCommand(request.RequestId).Payload==request.Payload&&repository.NextFailureNotice() is null,
                        "三次重试复用请求和参数，未到上限不发通知");
                else Check(latest is {NotBefore:null,Failures:0}&&receipt is {State:"Completed",Code:"Failed"}
                    &&repository.NextFailureNotice()?.Id==execution,"第四次失败停止补跑并持久化通知");
            }

            // 结果未知不是普通可重试失败；即使尚有重试额度，也必须终止原命令以避免重复副作用。
            var unknownPath=Path.Combine(root,"runner-outcome-unknown.db");
            using(var unknownRepository=new EncryptedTaskRepository(unknownPath,key))
            {
                unknownRepository.RegisterDefault("test",new("unknown","结果未知任务","0 0 0 * * ?","China Standard Time",true));
                var unknownPlan=unknownRepository.Plans().Single();
                var unknownRequest=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","test","unknown",Payload:"synthetic");
                unknownRepository.AcceptCommand(unknownRequest,"admin");
                var unknownExecution=unknownRepository.Begin(unknownPlan,DateTimeOffset.UtcNow,unknownRequest.RequestId);
                unknownRepository.End(unknownExecution,unknownPlan,new("Failed","RunnerOutcomeUnknown"),unknownRequest.RequestId);
                Check(unknownRepository.FindCommand(unknownRequest.RequestId,"admin") is {State:"Completed",Code:"Failed",ExecutionId:not null}
                    &&unknownRepository.PendingRuns().Count==0
                    &&unknownRepository.FindExecution(unknownExecution) is {Result:"Failed",Checkpoint:"RunnerOutcomeUnknown"}
                    &&unknownRepository.NextFailureNotice()?.Id==unknownExecution,
                    "Runner结果未知立即终结原命令、保留执行证据并发出失败通知，不自动重放");
            }

            using var reopened=new EncryptedTaskRepository(path,key);
            var email=new Channel("email",true);var ding=new Channel("dingtalk",true);var wechat=new Channel("pushplus",false);
            var notifications=new WorkerNotificationGateway(new EmailNotificationService(root),new NotificationDispatcher([email,ding,wechat]));
            var notice=reopened.NextFailureNotice()!;
            reopened.MarkFailureChannel(notice,email.Descriptor.Id);
            await using var catalog=new BackgroundPluginCatalog();
            await using var runtime=new BackgroundRuntime(reopened,catalog,notifications:notifications);
            await runtime.StartAsync(default);
            using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(reopened.NextFailureNotice() is not null)await Task.Delay(25,wait.Token);
            Check(ding.Sends==1&&email.Sends==0&&wechat.Sends==0,"重启恢复通知，跳过已尝试和未启用渠道");
            var reply=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetJobs"),new("reader",false),default);
            Check(reply.Jobs!.Single() is {LastStartedAt:not null,LastResult:"Failed",PlanState:"Unavailable"},"服务计划显示持久执行时间及处理器不可用状态");
        }
        finally {CryptographicOperations.ZeroMemory(key);}
    }

    private static void Check(bool ok,string message)
    {
        if(!ok)throw new InvalidOperationException(message);
        Console.WriteLine("PASS "+message);
    }

    private sealed class Channel(string id,bool enabled):INotificationChannel
    {
        public NotificationChannelDescriptor Descriptor {get;}=new(id,id);
        public int Sends {get;private set;}
        public Task<NotificationChannelStatus> StatusAsync(string profileId,CancellationToken token)=>
            Task.FromResult(new NotificationChannelStatus(id,profileId,enabled,true,"Ok"));
        public Task<NotificationDeliveryResult> SendAsync(string profileId,NotificationEnvelope message,CancellationToken token)
        {
            Sends++;
            return Task.FromResult(new NotificationDeliveryResult(id,NotificationDeliveryState.Accepted,"Accepted",message.MessageId));
        }
    }
}
