using System.Collections.Concurrent;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class TaskCenterRefreshChecks
{
    internal static async Task RunAsync()
    {
        var control=new Control();
        using var stop=new CancellationTokenSource();
        var pipe="CRS.TaskCenter.Refresh."+Guid.NewGuid().ToString("N");
        var listening=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var model=new BackgroundServiceViewModel(stop.Token,new BackgroundPipeClient(pipe));
            var refresh=model.RefreshCommand.ExecuteAsync(null);
            await control.HealthRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(model.History.Count!=1 || model.History[0].Result!="Running" || model.PendingRequests.Count!=1 || refresh.IsCompleted)
                throw new Exception("慢健康查询仍阻挡运行记录或待执行请求显示。");
            if(control.Calls.TakeWhile(x=>x!="GetFilteredHistory").Any(x=>x is "GetHealth" or "GetJobs"))throw new Exception("历史查询仍等待健康或计划。");
            control.ReleaseHealth.TrySetResult();await refresh;
            var healthCount=control.Calls.Count(x=>x=="GetHealth");
            control.Record=control.Record with{EndedUtc=DateTimeOffset.UtcNow,Result="Succeeded"};
            await model.RefreshRecentHistoryAsync();
            if(model.History.Single().Result!="Succeeded" || control.Calls.Count(x=>x=="GetHealth")!=healthCount)
                throw new Exception("执行首页刷新仍依赖健康/计划查询或未更新结果。");
            model.HistoryJob="missing";await model.FilterHistoryCommand.ExecuteAsync(null);
            if(model.History.Count!=0 || model.PendingRequests.Count!=0)throw new Exception("空筛选遗留旧执行或排队请求。");
            control.ProvidePending=false;model.HistoryJob="";await model.FilterHistoryCommand.ExecuteAsync(null);
            if(!model.PendingStatus.Contains("尚未提供"))throw new Exception("旧服务缺少排队字段被当作零。");
            await FooterRefreshAsync(control,new BackgroundPipeClient(pipe),stop.Token);
            Console.WriteLine("PASS 执行记录：慢健康不挡首屏、运行与待执行分别显示、自动刷新和空筛选清理。");
        }
        finally{control.ReleaseHealth.TrySetResult();stop.Cancel();try{await listening;}catch(OperationCanceledException){}}
    }
    private static async Task FooterRefreshAsync(Control control,BackgroundPipeClient client,CancellationToken token)
    {
        control.BlockHistory=true;
        var baseline=control.Calls.Count(x=>x=="GetHealth");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Windows.Threading.Dispatcher? dispatcher=null;
        ShellStatusBarViewModel? footer=null;
        var thread=new Thread(()=>
        {
            dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            footer=new(client);footer.Start(token);ready.TrySetResult();
            System.Windows.Threading.Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline=Environment.TickCount64+7000;
            while(control.Calls.Count(x=>x=="GetHealth")<baseline+2 && Environment.TickCount64<deadline)await Task.Delay(100,token);
            if(control.Calls.Count(x=>x=="GetHealth")<baseline+2 || control.ReleaseHistory.Task.IsCompleted)
                throw new Exception("慢同步历史查询仍阻挡底栏3秒健康采样。");
            Console.WriteLine("PASS 底栏：同步历史挂起期间仍持续查询运行/排队状态。");
        }
        finally
        {
            control.ReleaseHistory.TrySetResult();
            if(dispatcher is not null)await dispatcher.InvokeAsync(()=>{footer?.Stop();dispatcher.InvokeShutdown();});
            thread.Join(3000);
        }
    }
    private sealed class Control:IBackgroundControl
    {
        internal readonly ConcurrentQueue<string> Calls=new();
        internal readonly TaskCompletionSource HealthRequested=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHealth=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseHistory=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockHistory;
        internal bool ProvidePending=true;
        internal ExecutionRecord Record=new(Guid.NewGuid().ToString("N"),"crs.national-team","market-activity-sync",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,null,"Running",null,1);
        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            Calls.Enqueue(request.Command);
            if(request.Command=="GetHealth")
            {
                HealthRequested.TrySetResult();await ReleaseHealth.Task.WaitAsync(token);
                return new(WorkerProtocol.Version,request.RequestId,"Ok",Health:new("Running",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,1));
            }
            if(request.Command=="GetJobs")return new(WorkerProtocol.Version,request.RequestId,"Ok",Jobs:[]);
            if(BlockHistory)await ReleaseHistory.Task.WaitAsync(token);
            var match=request.JobId is null || request.JobId==Record.JobId;
            return new(WorkerProtocol.Version,request.RequestId,"Ok",History:match?[Record]:[],AppliedResultFilter:request.ResultFilter,
                PendingRequests:!ProvidePending?null:match?[new(Guid.NewGuid(),Record.PluginId,Record.JobId)]:[]);
        }
    }
}
