using System.Security.Cryptography;
using System.Text.Json;
using CRS.Maintenance;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

internal static class DataCleanupPipeChecks
{
    public static async Task RunAsync()
    {
        var passed=0;void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS "+text);}
        var root=Path.Combine(Path.GetTempPath(),"CRS.CleanupPipe",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            using var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler-config.db"),key);
            await using var catalog=new BackgroundPluginCatalog();await using var cleanup=new WorkerDataCleanup(root,null,new WorkerLogSettingsStore(root,key));
            await using var runtime=new BackgroundRuntime(repository,catalog,cleanup:cleanup);await runtime.StartAsync(default);
            var admin=new BackgroundCaller("test-admin",true);var reader=new BackgroundCaller("test-reader",false);
            var pause=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"PauseAutoCleanup");
            Check((await runtime.HandleAsync(pause,reader,default)).Code=="Forbidden","普通用户不能暂停后台自动清理");
            Check((await runtime.HandleAsync(pause with{Payload="{}"},admin,default)).Code=="InvalidRequest","维护暂停命令不接受额外清理参数");
            Check((await runtime.HandleAsync(pause,admin,default)).Code=="Ok","管理员可暂停自动清理以准备维护");
            var preview=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"BeginDataCleanupPreview",Payload:JsonSerializer.Serialize(new DataCleanupOptions()));
            Check((await runtime.HandleAsync(preview,reader,default)).Code=="Forbidden","普通用户不能扫描后台私有资料");
            Check((await runtime.HandleAsync(preview with{Payload="{\"Path\":\"C:\\\\outside\"}"},admin,default)).Code=="CleanupUnavailable","客户端不能指定清理路径");
            Check((await runtime.HandleAsync(preview with{Payload="{\"LogRetentionDays\":14,\"LogRetentionDays\":1}"},admin,default)).Code=="InvalidRequest","拒绝重复选项字段");
            Check((await runtime.HandleAsync(preview with{Payload="{\"HistoricalBackupsToKeep\":-1}"},admin,default)).Code=="CleanupUnavailable","服务端拒绝负数的额外备份保留量");
            Check((await runtime.HandleAsync(preview,admin,default)).Code=="Ok","管理员可开始有界异步扫描");
            Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupStatus",RelatedRequestId:preview.RequestId),new("other-admin",true),default)).Code=="Forbidden","另一管理员身份不能冒领操作记录");
            using var stop=new CancellationTokenSource();var pipe="CRS.Cleanup."+Guid.NewGuid().ToString("N");var running=new BackgroundPipeServer(runtime,pipe).RunAsync(stop.Token);
            try
            {
                var client=new BackgroundPipeClient(pipe);var until=DateTimeOffset.UtcNow.AddSeconds(20);DataCleanupStatus status;
                // 新建该连接实际身份的预览；后台序列化扫描，不借用合成身份的预览。
                while(cleanup.Status(preview.RequestId,admin.Sid).State=="Scanning")await Task.Delay(20);
                var request=preview with{RequestId=Guid.NewGuid()};var reply=await client.SendAsync(request);
                Check(reply.Code=="Ok","真实 Windows 管道承认新的预览命令");
                do
                {
                    reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupStatus",RelatedRequestId:request.RequestId));
                    status=JsonSerializer.Deserialize<DataCleanupStatus>(reply.Payload!)!;if(status.State!="Scanning")break;
                    if(DateTimeOffset.UtcNow>until)throw new TimeoutException();await Task.Delay(20);
                }while(true);
                Check(status.State=="Ready"&&!status.CanExecute,"实际后台目录没有候选时明确为不可执行");
                var log=Path.Combine(root,"logs/service-20200101.jsonl");Directory.CreateDirectory(Path.GetDirectoryName(log)!);File.WriteAllText(log,"synthetic expired log");File.SetLastWriteTimeUtc(log,DateTime.UtcNow.AddYears(-2));
                request=preview with{RequestId=Guid.NewGuid()};await client.SendAsync(request);
                do
                {reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupStatus",RelatedRequestId:request.RequestId));status=JsonSerializer.Deserialize<DataCleanupStatus>(reply.Payload!)!;if(status.State!="Scanning")break;await Task.Delay(20);}while(DateTimeOffset.UtcNow<until);
                Check(status.CanExecute,"实际管道扫描可识别合成过期日志");
                Check(status.Progress is{ScannedFiles:1,ScannedBytes:>0},"真实管道保留扫描文件数与已扫描数据量");
                var execution=Guid.NewGuid();var timer=System.Diagnostics.Stopwatch.StartNew();reply=await client.SendAsync(new(WorkerProtocol.Version,execution,"ExecuteDataCleanup",RelatedRequestId:request.RequestId));
                Check(reply.Code=="Ok"&&timer.Elapsed<TimeSpan.FromSeconds(5),"删除命令经管理员控制通道快速受理");
                do
                {reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupStatus",RelatedRequestId:execution));status=JsonSerializer.Deserialize<DataCleanupStatus>(reply.Payload!)!;if(status.State!="Running")break;await Task.Delay(20);}while(DateTimeOffset.UtcNow<until);
                Check(status.State=="Completed"&&status.DeletedFiles==1&&!File.Exists(log),"真实管道返回后台实际清理结果");
                Check((await client.QueryAsync("GetHealth")).Health?.State=="Running","清理结束后后台健康和调度继续");
                var taskRequest=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"SubmitDataCleanupTask");
                Check((await runtime.HandleAsync(taskRequest,reader,default)).Code=="Forbidden","普通用户不能提交后台清理任务");
                Check((await client.SendAsync(taskRequest with{Payload="{}"})).Code=="InvalidRequest","完整后台任务不接受客户端路径或清理选项");
                timer.Restart();reply=await client.SendAsync(taskRequest);
                Check(reply.Code=="Ok"&&timer.Elapsed<TimeSpan.FromSeconds(5),"真实管理管道快速受理完整后台清理任务");
                var overview=JsonSerializer.Deserialize<DataCleanupTaskOverview>(reply.Payload!)!;
                var taskId=overview.Current?.Id??overview.LatestResult!.Id;
                do{reply=await client.QueryAsync("GetDataCleanupTasks");overview=JsonSerializer.Deserialize<DataCleanupTaskOverview>(reply.Payload!)!;if(overview.Current is null)break;await Task.Delay(10);}while(DateTimeOffset.UtcNow<until);
                Check(overview.LatestResult is{State:"Completed"}&&overview.Notification?.Id==taskId,"前台不保留操作编号也可读取后台完成通知");
                Check((await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetDataCleanupTasks"),reader,default)).Code=="Forbidden","非管理员不能读取后台私有清理摘要");
                reply=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"AcknowledgeDataCleanupTask",RelatedRequestId:taskId));
                Check(reply.Code=="Ok"&&JsonSerializer.Deserialize<DataCleanupTaskOverview>(reply.Payload!)!.Notification is null,"通知确认经管理管道持久化，之后不再重复提醒");
                var history=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),DataCleanupTask.HistoryCommand);
                Check((await runtime.HandleAsync(history,reader,default)).Code=="Forbidden","普通用户不能读取清理历史");
                var record=(await client.QueryAsync(DataCleanupTask.HistoryCommand)).History?.Single();
                Check(record is{JobId:DataCleanupTask.JobId,Result:"Succeeded"}&&record.ExecutionId=="cleanup:"+taskId.ToString("N"),
                    "真实管道返回已确认通知的清理执行记录");
                await CheckLogRetentionAsync(client,runtime);
                Check((await client.QueryAsync("PauseAutoCleanup")).Code=="Ok","真实管理员控制管道可确认自动清理暂停");
            }
            finally{stop.Cancel();try{await running;}catch(OperationCanceledException){}}
        }
        finally{CryptographicOperations.ZeroMemory(key);}
        Console.WriteLine($"资料清理后台与真实管道验证通过：{passed} 项。");

        async Task CheckLogRetentionAsync(BackgroundPipeClient client,BackgroundRuntime runtime)
        {
            BackgroundRequest Save(WorkerLogSettings settings)=>new(WorkerProtocol.Version,Guid.NewGuid(),WorkerProtocol.SaveWorkerLogSettings,
                Payload:JsonSerializer.Serialize(settings));
            var read=await client.QueryAsync(WorkerProtocol.GetWorkerLogSettings);
            var initial=JsonSerializer.Deserialize<WorkerLogSettings>(read.Payload!)!;
            Check(read.Code=="Ok"&&initial==new WorkerLogSettings(),"未配置时后台日志默认保留 14 天");
            var update=Save(initial with {RetentionDays=7});
            Check((await runtime.HandleAsync(update,new("reader",false),default)).Code=="Forbidden"
                &&(await client.SendAsync(Save(initial with {RetentionDays=366}))).Code=="InvalidLogSettings","后台拒绝普通用户和超出范围的保留策略");
            var saved=await client.SendAsync(update);
            Check(saved.Code=="Ok"&&new WorkerLogSettingsStore(root,key).Read()==new WorkerLogSettings(7,1)
                &&(await client.SendAsync(Save(initial with {RetentionDays=60}))).Code=="LogSettingsConflict","管理员可保存策略，重读仍有效并拒绝过期修订");

            var oldTime=CRS.Timing.BeijingClock.Now.AddDays(-8);
            var old=Path.Combine(root,"logs",FormattableString.Invariant($"service-{oldTime:yyyyMMdd}.jsonl"));
            var recent=Path.Combine(root,"logs",FormattableString.Invariant($"service-{CRS.Timing.BeijingClock.Now:yyyyMMdd}.jsonl"));
            File.WriteAllText(old,"synthetic old log");File.SetLastWriteTimeUtc(old,oldTime.UtcDateTime);
            File.WriteAllText(recent,"synthetic current log");
            var submit=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"SubmitDataCleanupTask"));
            Check(submit.Code=="Ok","配置策略后可提交后台清理");
            var limit=DateTimeOffset.UtcNow.AddSeconds(20);
            DataCleanupTaskOverview? overview=null;
            while(DateTimeOffset.UtcNow<limit)
            {
                var reply=await client.QueryAsync("GetDataCleanupTasks");
                if(reply is {Code:"Ok",Payload:{} payload})
                {
                    overview=JsonSerializer.Deserialize<DataCleanupTaskOverview>(payload)!;
                    if(overview.Current is null)break;
                }
                else if(reply.Code!="RateLimited")throw new InvalidOperationException("读取清理结果失败："+reply.Code);
                await Task.Delay(200);
            }
            Check(overview is {Current:null,AutomaticPlan.LogRetentionDays:7,LatestResult:{State:"Completed",DeletedFiles:1}}
                &&!File.Exists(old)&&File.Exists(recent),"资料清理按配置删除过期文件并保留当天日志");
        }
    }
}
