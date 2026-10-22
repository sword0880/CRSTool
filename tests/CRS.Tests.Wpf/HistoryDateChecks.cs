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
            await model.OpenExecutionHistoryAsync(false);
            Check(model.HistoryStartDate==today&&model.HistoryEndDate==today&&control.Calls==0,"默认起止日期均为当天，进入页面不自动查询");
            await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==75&&control.Calls==2,"点击查询完整读取当天75条，不需要手动翻页");
            var count=control.Calls;
            model.HistoryStartDate=today.AddDays(-1);
            Check(control.Calls==count&&model.History.Count==75,"修改日期不自动查询或覆盖已有结果");
            await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==150&&control.Calls==count+4&&model.History.Select(x=>x.StartedUtc.Date).Distinct().Count()==2
                &&model.History[0].StartedUtc.Date==today&&model.History[^1].StartedUtc.Date==today.AddDays(-1),"日期范围包含首尾两日，收齐各日分页并按开始时间倒序");
            Check(model.History.Any(x=>x.StartedUtc.TimeOfDay==TimeSpan.Zero)
                &&model.History.Any(x=>x.StartedUtc.TimeOfDay==TimeSpan.FromDays(1)-TimeSpan.FromTicks(1)),"包含午夜和结束日期最后一刻");
            count=control.Calls;model.HistoryStartDate=today.AddDays(1);await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0&&control.Calls==count&&model.Status.Contains("不能晚于"),"倒置日期明确提示，清除旧结果且不请求后台");
            model.HistoryStartDate=null;await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(control.Calls==count&&model.Status.Contains("请选择"),"缺失日期不发起查询");
            model.HistoryStartDate=today.AddDays(-1);
            model.HistoryJob="missing";await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0,"无匹配日期与任务不残留旧记录");
            model.HistoryJob="";model.HistoryStartDate=model.HistoryEndDate=today;await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==75,"手动选择同日仅查询一天");
            control.FailDate=DateOnly.FromDateTime(today.AddDays(-1));model.HistoryStartDate=today.AddDays(-1);
            await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0&&model.Status.Contains("不可用"),"范围中途失败不展示半份结果");
            control.FailDate=null;model.HistoryStartDate=today;
            control.Unsupported=true;await model.FilterHistoryCommand.ExecuteAsync(null);
            Check(model.History.Count==0&&model.Status.Contains("尚不支持按日期"),"旧后台明确提示不支持，不把跨日记录当成当天结果");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static void Check(bool ok,string text)
    {if(!ok)throw new Exception(text);Console.WriteLine("PASS 执行记录日期界面："+text);}
    private sealed class Control:IBackgroundControl
    {
        public int Calls;public bool Unsupported;public DateOnly? FailDate;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            Calls++;
            if(Unsupported)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownCommand"));
            if(request.Command!="GetHistoryByDate")throw new Exception("未使用日期查询接口");
            var date=DateOnly.ParseExact(request.Payload!,"yyyy-MM-dd");
            if(date==FailDate)return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Unavailable"));
            var offset=request.Cursor is null?0:int.Parse(request.Cursor);var end=Math.Min(offset+50,75);
            var rows=request.JobId=="missing"?Array.Empty<ExecutionRecord>():Enumerable.Range(offset,end-offset).Select(i=>
            {
                var time=new DateTimeOffset(date.ToDateTime(i==0?TimeOnly.MinValue:i==74?TimeOnly.MaxValue:new TimeOnly(12,0)),TimeSpan.FromHours(8));
                return new ExecutionRecord(date+"-"+i,"plugin","one",time,time,time,"Succeeded",null,1);
            }).ToArray();
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",History:rows,
                NextCursor:rows.Length>0&&end<75?end.ToString():null,Payload:request.Payload,AppliedResultFilter:request.ResultFilter,PendingRequests:[]));
        }
    }
}
