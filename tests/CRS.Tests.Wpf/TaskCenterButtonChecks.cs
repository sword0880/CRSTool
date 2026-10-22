using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class TaskCenterButtonChecks
{
    public static async Task RunAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var control=new Control();var pipe="CRS.TaskButtons."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var model=new BackgroundServiceViewModel(stop.Token,new(pipe));
            await model.RefreshCommand.ExecuteAsync(null);
            await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==2&&model.HasMoreJobs&&!model.HasUnconfirmedRequest,"分页有下一页才提供入口，正常连接无确认提示");
            await model.MoreJobsCommand.ExecuteAsync(null);
            Check(model.History.Count==2&&model.Jobs.Count==2&&!model.HasMoreJobs,"读完下一页后隐藏入口且不漏数据");
            await model.RunNowCommand.ExecuteAsync(null);
            Check(model.HasUnconfirmedRequest,"后台已受理但连接断开时显示重新确认入口");
            await model.PauseCommand.ExecuteAsync(null);
            Check(control.Mutations.Count==1,"未确认时阻止创建其它操作");
            await model.ConfirmPendingCommand.ExecuteAsync(null);
            Check(!model.HasUnconfirmedRequest&&control.Mutations.Count==2&&control.Mutations[0]==control.Mutations[1],"重新确认沿用原编号和原参数，确认后隐藏入口");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static void Check(bool ok,string name)
    {if(!ok)throw new Exception(name);Console.WriteLine("PASS 任务中心按钮："+name);}
    private sealed class Control:IBackgroundControl
    {
        public List<BackgroundRequest> Mutations=[];
        private readonly DateTimeOffset now=CRS.Timing.BeijingClock.Now;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            BackgroundResponse response;
            if(request.Command is "RunNow" or "Pause")
            {
                Mutations.Add(request);
                if(Mutations.Count==1)throw new System.IO.IOException("模拟受理后的连接中断");
                response=new(WorkerProtocol.Version,request.RequestId,"Succeeded",Receipt:new(request.RequestId,"Completed","Succeeded",null));
            }
            else response=request.Command switch
            {
                "GetHealth"=>new(WorkerProtocol.Version,request.RequestId,"Ok",Health:new("Running",now,now,1)),
                "GetJobs"=>new(WorkerProtocol.Version,request.RequestId,"Ok",Jobs:[new("plugin",request.Cursor is null?"one":"two","任务","0 * * * * ?","China Standard Time",true,1,false)],NextCursor:request.Cursor is null?"plans-2":null),
                "GetTaskActivity"=>new(WorkerProtocol.Version,request.RequestId,"Ok",TaskActivity:new(now,[],0,0,0)),
                "GetHistoryByDate"=>new(WorkerProtocol.Version,request.RequestId,"Ok",History:[new(request.Cursor is null?"one":"two","plugin","one",now,now,now,"Succeeded",null,1)],NextCursor:request.Cursor is null?"history-2":null,PendingRequests:[],Payload:request.Payload),
                _=>throw new Exception("非预期命令："+request.Command)
            };
            return Task.FromResult(response);
        }
    }
}
