using System.Diagnostics;
using System.Text.Json;
using CRS.DesktopClient.Services;
using CRS.Maintenance;
using CRS.Worker.Ipc;

internal static class DataCleanupGatewayChecks
{
    private sealed class Observer(Action<DataCleanupStatus> report):IProgress<DataCleanupStatus>
    {public void Report(DataCleanupStatus value)=>report(value);}

    internal static async Task RunAsync()
    {
        var elapsed=Stopwatch.StartNew();var id=Guid.Empty;var submissions=0;var reads=0;var reports=0;var afterBoundary=false;
        var gateway=new WorkerDataCleanupGateway((request,token)=>
        {
            token.ThrowIfCancellationRequested();
            if(request.Command=="BeginDataCleanupPreview"){id=request.RequestId;submissions++;}
            else if(request.Command!="GetDataCleanupStatus"||request.RelatedRequestId!=id)throw new Exception("跟踪改变了操作编号或重复提交操作。");
            else reads++;
            var done=elapsed.Elapsed>=TimeSpan.FromSeconds(185);
            var status=new DataCleanupStatus(id,done?"Ready":"Scanning",CRS.Timing.BeijingClock.Now,
                done?CRS.Timing.BeijingClock.Now.AddMinutes(15):null,[],0,0,"合成扫描进度",false)
                {Progress=new("ScanningBackups","maintenance/synthetic-only",reads,reads*1024)};
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(status)));
        });
        var observer=new Observer(status=>
        {
            reports++;if(status.State=="Scanning"&&elapsed.Elapsed>TimeSpan.FromMinutes(3))afterBoundary=true;
            if(reports%30==0)Console.WriteLine($"合成跟踪仍在刷新：{elapsed.Elapsed.TotalSeconds:F0}秒 / {reads}次状态查询。");
        });
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(210));
        var result=await gateway.PreviewAsync(new(),observer,deadline.Token);
        if(result.State!="Ready"||!afterBoundary||submissions!=1||reads<181||reports<181)
            throw new Exception("超过三分钟后停止跟踪或错误提交了新扫描。");
        Console.WriteLine("PASS 实际等待超过三分钟后继续刷新，直至同一合成扫描返回预览；只提交一次扫描，无真实管道或文件操作。");
        await VerifyCancellationAsync();
    }

    private static async Task VerifyCancellationAsync()
    {
        var id=Guid.NewGuid();var reports=0;using var stopping=new CancellationTokenSource();
        var gateway=new WorkerDataCleanupGateway((request,token)=>Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",
            Payload:JsonSerializer.Serialize(new DataCleanupStatus(id,"Running",CRS.Timing.BeijingClock.Now,null,[],0,0,"合成后台仍在执行")))));
        try{await gateway.StatusAsync(id,new Observer(_=>{reports++;stopping.Cancel();}),stopping.Token);throw new Exception("停止等待未取消前台跟踪。");}
        catch(OperationCanceledException) when(stopping.IsCancellationRequested){}
        if(reports!=1)throw new Exception("停止等待后仍在产生进度回调。");
        Console.WriteLine("PASS 前台取消只结束状态跟踪，不发送取消后台操作的命令。");
    }
}
