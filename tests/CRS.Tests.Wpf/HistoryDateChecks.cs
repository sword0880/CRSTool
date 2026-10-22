using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class HistoryDateChecks
{
    public static async Task RunAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var control=new Control();
        var pipe="CRS.Wpf.HistoryDate."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var model=new BackgroundServiceViewModel(stop.Token,new(pipe));
            var today=CRS.Timing.BeijingClock.Now.Date;
            await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.HistoryDate==today&&model.History.Count==75&&control.Calls==2,"默认完整加载北京时间当天75条，不需要手动翻页");
            model.HistoryDate=today.AddDays(-1);await model.FilterHistoryCommand.ExecuteAsync(null);
            var count=control.Calls;await model.RefreshRecentHistoryAsync();
            Check(model.History.Count==75&&model.History.All(x=>x.StartedUtc.Date==today.AddDays(-1))&&control.Calls==count,"更换日期查询并保留历史日期结果");
            model.HistoryJob="missing";await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0,"无匹配日期与任务不残留旧记录");
            model.HistoryJob="";await model.TodayHistoryCommand.ExecuteAsync(null);
            Check(model.HistoryDate==today&&model.History.Count==75,"今天入口恢复当天查询");
            control.Unsupported=true;await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0&&model.Status.Contains("尚不支持按日期"),"旧后台明确提示不支持，不把跨日记录当成当天结果");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static void Check(bool ok,string text)
    {if(!ok)throw new Exception(text);Console.WriteLine("PASS 执行记录日期界面："+text);}
    private sealed class Control:IBackgroundControl
    {
        public int Calls;public bool Unsupported;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            Calls++;
            if(Unsupported)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownCommand"));
            if(request.Command!="GetHistoryByDate")throw new Exception("未使用日期查询接口");
            var date=DateOnly.ParseExact(request.Payload!,"yyyy-MM-dd");
            var offset=request.Cursor is null?0:int.Parse(request.Cursor);var end=Math.Min(offset+50,75);
            var rows=request.JobId=="missing"?Array.Empty<ExecutionRecord>():Enumerable.Range(offset,end-offset).Select(i=>
            {
                var time=new DateTimeOffset(date.ToDateTime(new TimeOnly(12,0)),TimeSpan.FromHours(8));
                return new ExecutionRecord(date+"-"+i,"plugin","one",time,time,time,"Succeeded",null,1);
            }).ToArray();
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",History:rows,
                NextCursor:rows.Length>0&&end<75?end.ToString():null,Payload:request.Payload,AppliedResultFilter:request.ResultFilter,PendingRequests:[]));
        }
    }
}
