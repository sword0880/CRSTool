using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
using CRS.Worker.Abstractions;

/// <summary>真实隔离管道验证请求身份及双回执，控制器不产生金融事实，不以合成成功代替正式登记。</summary>
internal static class HistoricalEquityCommandChecks
{
    private const string Definition="cnd-600153-20251231-pdf-v1";
    public static async Task RunAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(45));var control=new Control();var pipe="CRS.HistoricalCommand."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);var passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS 历史登记回执："+label);}
        async Task Invalid(Func<Task> action){try{await action();throw new Exception("预期拒绝未发生。");}catch(InvalidDataException){}}
        try
        {
            var reader=new WorkerEtfWorkspace(new(pipe));
            await Invalid(()=>reader.ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",false,stop.Token));Check(control.Runs==0,"未确认原文不发送命令");
            control.Drop=true;
            try{await reader.ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",true,stop.Token);throw new Exception("受理丢失未报告未知。");}catch(BackgroundServiceException){}
            var original=control.Known;
            var pending=await reader.ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",true,stop.Token);
            Check(pending is{State:"Pending",Code:"RetryDeferred"}&&pending.RequestId==original&&control.Runs==1&&control.Reads>0,"受理响应丢失后先回查原ID，延期不重复RunNow");
            await Invalid(()=>reader.ImportHistoricalEquityPdfAsync(Definition,"changed","reason",true,stop.Token));Check(control.Runs==1,"未确认期间不能换参数新开任务");
            var reopened=new WorkerEtfWorkspace(new(pipe));var recovered=await reopened.ImportHistoricalEquityPdfAsync(Definition,"new reviewer","new reason",true,stop.Token);
            Check(recovered.ExistingRequest&&recovered.RequestId==original&&control.Runs==1&&control.FilteredByJob,"重开客户端按任务过滤找回待处理ID，不以未知参数重放");
            control.Completed=true;control.Business=false;
            var uncertain=await reader.HistoricalEquityStatusAsync(original,stop.Token);
            Check(uncertain is{State:"Unconfirmed",Code:"BusinessReceiptMissing"},"宿主成功而业务回执缺失不算登记完成");
            var observedAgain=await reopened.ImportHistoricalEquityPdfAsync(Definition,"changed observed","different reason",true,stop.Token);
            Check(observedAgain is{State:"Unconfirmed",Code:"BusinessReceiptMissing",ExistingRequest:true}&&observedAgain.RequestId==original&&control.Runs==1,"重开客户端观察的旧ID在业务回执缺失时仍保持，不新建任务");
            await Invalid(()=>reader.ImportHistoricalEquityPdfAsync(Definition,"changed","reason",true,stop.Token));Check(control.Runs==1,"业务回执缺失仍保留原参数约束");
            control.Business=true;var done=await reader.HistoricalEquityStatusAsync(original,stop.Token);
            Check(done is{State:"Completed",Code:"Succeeded"}&&(await reader.HistoricalEquityReceiptAsync(original,stop.Token))?.ReviewedAccounts==4,"宿主与业务回执同ID后才确认完成");
            control.BadReceipt=true;await Invalid(()=>reader.HistoricalEquityReceiptAsync(original,stop.Token));Check(true,"异请求业务回执拒绝");control.BadReceipt=false;
            control.Reset();control.UnknownFirst=true;control.Drop=true;var absentReader=new WorkerEtfWorkspace(new(pipe));
            try{await absentReader.ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",true,stop.Token);throw new Exception("未知受理未报告。");}catch(BackgroundServiceException){}
            var firstId=control.Submitted[0];var accepted=await absentReader.ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",true,stop.Token);
            Check(control.Submitted.Count==2&&control.Submitted.All(x=>x==firstId)&&accepted.RequestId==firstId,"服务明确UnknownRequest后仍沿原ID原参数提交");
            control.Reset();control.NoPendingCapability=true;await Invalid(()=>new WorkerEtfWorkspace(new(pipe)).ImportHistoricalEquityPdfAsync(Definition,"reviewer","reason",true,stop.Token));
            Check(control.Runs==0,"缺待处理队列能力不猜测为空，不重复提交");
            Console.WriteLine($"PASS 历史登记回执合计 {passed}；全部隔离控制器，不代表来源或金融事实验收。");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class Control:IBackgroundControl
    {
        public Guid Known;public bool Drop,Completed,Business,BadReceipt,UnknownFirst,NoPendingCapability,FilteredByJob;public int Runs,Reads;public List<Guid> Submitted=[];
        public void Reset(){Known=Guid.Empty;Drop=Completed=Business=BadReceipt=UnknownFirst=NoPendingCapability=FilteredByJob=false;Runs=Reads=0;Submitted.Clear();}
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            BackgroundResponse Reply(string code,CommandReceipt? receipt=null,string? payload=null,IReadOnlyList<BackgroundPendingRequest>? pending=null)
                =>new(WorkerProtocol.Version,request.RequestId,code,Receipt:receipt,Payload:payload,PendingRequests:pending);
            CommandReceipt Receipt()=>new(Known,Completed?"Completed":"Pending",Completed?"Succeeded":"RetryDeferred",null);
            BackgroundResponse response;
            if(request.Command=="GetFilteredHistory")
            {
                FilteredByJob=request.JobId=="equity-history-import";
                response=Reply("Ok",pending:NoPendingCapability?null:Known!=Guid.Empty&&!Completed?[new(Known,"crs.national-team","equity-history-import")]:[]);
            }
            else if(request.Command=="RunNow")
            {
                Runs++;Submitted.Add(request.RequestId);if(!UnknownFirst||Runs>1)Known=request.RequestId;
                response=Drop?Reply("RequestTimedOut"):Reply("Accepted",Receipt());Drop=false;
            }
            else if(request.Command=="GetCommand"){Reads++;response=Known==Guid.Empty?Reply("UnknownRequest"):Reply("Ok",Receipt());}
            else
            {
                var query=EtfBusinessProtocol.ParseRequest(request.Payload!);
                var rows=Business?[new HistoricalEquityReceipt(BadReceipt?Guid.NewGuid():Known,Definition,"synthetic-disclosure","Imported",4,CRS.Timing.BeijingClock.Now,new string('A',64),new string('B',64))]:Array.Empty<HistoricalEquityReceipt>();
                response=query.Operation=="release-query"?Reply("Ok",payload:JsonSerializer.Serialize("Released"))
                    :Reply("Ok",payload:JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(rows),"synthetic",null){MaximumPageCount=256,TotalRows=rows.Length}));
            }
            return Task.FromResult(response);
        }
    }
}
