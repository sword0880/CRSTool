using CRS.DesktopClient.ViewModels;
using CRS.Worker.Ipc;
using CRS.Maintenance;
internal static class TaskActivityChecks
{
    public static async Task RunAsync()
    {
        var control=new Control();using var stop=new CancellationTokenSource();
        var pipe="CRS.TaskActivity."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var gateway=new CleanupTaskModelChecks.Gateway();var cleanup=new DataCleanupViewModel(gateway);
            var model=new BackgroundServiceViewModel(stop.Token,new(pipe),cleanup);
            await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==1&&model.WaitingTasks.Count==1&&model.PendingTasks.Count==1,"完整收齐三个状态的多页结果");
            Check(model.PendingTasks[0].ReasonText.Contains("重试")&&model.PendingTasks[0].EarliestText.Contains("2026-10-21"),"等待原因和北京时间可读");
            control.Fail=true;await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==1&&model.ActivityStatus.Contains("上次快照"),"连接错误保留旧快照并明确标记未更新");
            control.Fail=false;control.Empty=true;await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==0&&model.WaitingTasks.Count==0&&model.PendingTasks.Count==0,"空结果清除所有旧任务");
            control.Unsupported=true;await model.RefreshTaskActivityAsync();
            Check(model.ActivityStatus.Contains("尚不支持"),"旧后台不被当作全部空闲");
            control.Unsupported=false;control.Empty=false;
            await cleanup.SubmitTaskCommand.ExecuteAsync(null);await model.RefreshTaskActivityAsync();
            Check(model.WaitingTasks.Count==2&&model.WaitingTasks.Single(x=>x.Row.JobId=="data-cleanup").Row.RequestId==cleanup.CurrentTask?.Id,
                "后台清理Queued出现在排队列表，与插件任务合并且保留原任务编号");
            var queued=gateway.Overview.Current!;
            gateway.Overview=gateway.Overview with{Current=queued with{State="Scanning",Progress=new("VerifyingBackups","private/path",859,119382731,VerifiedBytes:4096)}};
            await model.RefreshTaskActivityAsync();
            var cleaning=model.RunningTasks.Single(x=>x.Row.JobId=="data-cleanup");
            Check(model.RunningTasks.Count==2&&model.WaitingTasks.Count==1&&model.ActivityStatus.Contains("执行中 2"),"扫描清理计入执行中，插件分页的数量校验仍然通过");
            Check(cleaning.ReasonText.Contains("核验")&&cleaning.ReasonText.Contains("859")&&!cleaning.ReasonText.Contains("private/path")&&cleaning.ActivityTime==queued.SubmittedAt,
                "显示实际扫描和校验量及提交时间，不把扫描量当成删除量或展示后台路径");
            gateway.FailRead=true;await model.RefreshTaskActivityAsync();
            Check(model.ActivityStatus.Contains("清理状态未确认")&&model.RunningTasks.Single(x=>x.Row.JobId=="data-cleanup").ReasonText.StartsWith("上次状态"),
                "清理查询失败标记旧快照，不因插件查询成功冒充最新清理状态");
            gateway.FailRead=false;gateway.Overview=gateway.Overview with{Current=queued with{State="Running",Progress=new("Deleting",null),DeletedFiles=5,DeletedBytes=1024}};
            await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Single(x=>x.Row.JobId=="data-cleanup").ReasonText.Contains("已清理 5"),"执行阶段展示真实已删除数量");
            await model.RefreshTaskActivityAsync();Check(model.RunningTasks.Count(x=>x.Row.JobId=="data-cleanup")==1,"连续刷新不重复累加清理任务");
            gateway.Complete();await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==1&&model.WaitingTasks.Count==1&&cleanup.HasTaskNotification,"清理结束后退出当前任务列表，完成通知保留且插件任务不受影响");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS 当前任务页面："+text);}
    private sealed class Control:IBackgroundControl
    {
        public bool Fail,Empty,Unsupported;
        private readonly DateTimeOffset observed=new(2026,10,21,22,0,0,TimeSpan.FromHours(8));
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(Unsupported)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownCommand"));
            if(Fail)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Unavailable"));
            var rows=new BackgroundTaskActivityRow[]{new("plugin","one","任务一","Running","Executing",StartedAt:observed),
                new("plugin","two","任务二","Waiting","PluginBusy",QueuePosition:1),
                new("plugin","three","任务三","Pending","RetryDeferred",NotBefore:observed.AddMinutes(5))};
            var offset=request.Cursor is null?0:int.Parse(request.Cursor);
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",NextCursor:Empty||offset==2?null:(offset+1).ToString(),
                TaskActivity:new(observed,Empty?[]:[rows[offset]],Empty?0:1,Empty?0:1,Empty?0:1)));
        }
    }
}
