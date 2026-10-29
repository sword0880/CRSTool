using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;

internal static class EtfQueryCancellationChecks
{
    public static async Task RunAsync()
    {
        using var stop=new CancellationTokenSource();var control=new Control();
        var pipe="CRS.Etf.Loading."+Guid.NewGuid().ToString("N");
        var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(pipe));
            using var old=new CancellationTokenSource();
            var history=reader.HistoryAsync(null,old.Token);
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));old.Cancel();
            try{await history;throw new Exception("旧查询未取消");}catch(OperationCanceledException){}
            await control.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS ETF管道：取消旧图表后后台收到断连取消，不等20秒超时。");
            var dashboard=await reader.DashboardAsync(null,default);
            if(dashboard?.History.Count!=27||dashboard.History.Select(x=>x.Date).Distinct().Count()!=27||reader.NetworkStatus!="synthetic")
                throw new Exception("整页分页数据丢失或状态错误");
            Console.WriteLine("PASS ETF管道：合并结果沿用原快照完整读取多页，不提高IPC额度。");
            control.OldService=true;
            if(await reader.DashboardAsync(null,default) is not null)throw new Exception("旧后台被伪装成空数据");
            Console.WriteLine("PASS ETF管道：旧后台明确回退独立查询，不返回伪造整页结果。");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class Control:IBackgroundControl
    {
        public bool OldService;
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(query.Operation=="query-capabilities")return new(WorkerProtocol.Version,request.RequestId,"UnknownQuery");
            if(query.Operation=="history")
            {
                Entered.TrySetResult();
                try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Cancelled.TrySetResult();throw;}
            }
            if(OldService)return new(WorkerProtocol.Version,request.RequestId,"UnknownQuery");
            var rows=Enumerable.Range(query.Offset,Math.Min(query.Count,27-query.Offset))
                .Select(i=>new EtfDashboardRow(History:new(new DateOnly(2026,9,1).AddDays(i),i,null,1,1))).ToArray();
            var next=query.Offset+rows.Length;
            var page=new EtfQueryPage(JsonSerializer.Serialize(rows),"synthetic-snapshot",next<27?next:null,JsonSerializer.Serialize("synthetic"));
            return new(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(page));
        }
    }
}
