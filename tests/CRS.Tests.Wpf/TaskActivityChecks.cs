using CRS.DesktopClient.ViewModels;
using CRS.Worker.Ipc;
internal static class TaskActivityChecks
{
    public static async Task RunAsync()
    {
        var control=new Control();using var stop=new CancellationTokenSource();
        var pipe="CRS.TaskActivity."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var model=new BackgroundServiceViewModel(stop.Token,new(pipe));
            await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==1&&model.WaitingTasks.Count==1&&model.PendingTasks.Count==1,"完整收齐三个状态的多页结果");
            Check(model.PendingTasks[0].ReasonText.Contains("重试")&&model.PendingTasks[0].EarliestText.Contains("2026-10-21"),"等待原因和北京时间可读");
            control.Fail=true;await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==1&&model.ActivityStatus.Contains("上次快照"),"连接错误保留旧快照并明确标记未更新");
            control.Fail=false;control.Empty=true;await model.RefreshTaskActivityAsync();
            Check(model.RunningTasks.Count==0&&model.WaitingTasks.Count==0&&model.PendingTasks.Count==0,"空结果清除所有旧任务");
            control.Unsupported=true;await model.RefreshTaskActivityAsync();
            Check(model.ActivityStatus.Contains("尚不支持"),"旧后台不被当作全部空闲");
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
