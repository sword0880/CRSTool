using System.IO;
using CRS.Plugins.NationalTeamWatch;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class EtfManualReceiptChecks
{
    public static async Task RunAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var control=new Control();var pipe="CRS.Etf.ManualReceipt."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var workspace=new WorkerEtfWorkspace(new(pipe));
            var day=new DateOnly(2026,10,21);
            await RejectAsync(()=>workspace.SyncMarketActivityAsync(day,day,stop.Token),"原请求已保留");
            if(control.Runs!=1||control.Reads!=1)throw new Exception("延期后仍持续等待，或重复创建了采集请求。");
            control.Mode="Duplicate";
            await RejectAsync(()=>workspace.SyncMarketActivityAsync(day,day,stop.Token),"执行中或待执行");
            control.Mode="Succeeded";
            await workspace.SyncMarketActivityAsync(day.AddDays(-1),day,stop.Token);
            if(control.Runs!=3)throw new Exception("已确认延期的请求阻止了后续操作。");
            Console.WriteLine("PASS ETF手动回执：延期及时提示并保留后台请求、重复请求不误称正在运行、成功单独确认。");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private static async Task RejectAsync(Func<Task> action,string message)
    {
        try{await action();}catch(InvalidDataException error) when(error.Message.Contains(message)){return;}
        throw new Exception("未明确显示任务等待状态："+message);
    }
    private sealed class Control:IBackgroundControl
    {
        public string Mode="Deferred";public int Runs,Reads;private Guid pending;
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            CommandReceipt receipt;
            if(request.Command=="RunNow")
            {
                Runs++;pending=request.RequestId;
                receipt=Mode switch
                {
                    "Duplicate"=>new(request.RequestId,"Completed","AlreadyRunning",null),
                    "Succeeded"=>new(request.RequestId,"Completed","Succeeded",Guid.NewGuid().ToString("N")),
                    _=>new(request.RequestId,"Pending","Accepted",null)
                };
                return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,receipt.Code,Receipt:receipt));
            }
            if(request.Command!="GetCommand"||request.RelatedRequestId!=pending)throw new Exception("查询未沿用原请求。");
            Reads++;receipt=new(pending,"Pending","RetryDeferred",null);
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Receipt:receipt));
        }
    }
}
