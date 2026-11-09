using System.IO;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.DesktopClient.ViewModels;
using CRS.Maintenance;

internal static class DataCleanupViewChecks
{
    internal sealed class PreviewGateway(string path):IDataCleanupGateway
    {
        public bool CanExecute=>true;
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>Task.FromResult(JsonSerializer.Deserialize<DataCleanupStatus>(File.ReadAllText(path))!);
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new InvalidOperationException("只读预览，不提供真实清理。");
        public Task<DataCleanupStatus> StatusAsync(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>throw new InvalidOperationException("只读预览，不提供真实清理。");
    }
    private sealed class Gateway:IDataCleanupGateway
    {
        public bool CanExecute=>true;public int Executions;public Guid Submitted;public DataCleanupOptions? Options;
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token){Options=options;return Task.FromResult(new DataCleanupStatus(Guid.NewGuid(),"Ready",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now.AddMinutes(15),[new("log","Logs","旧日志","logs/old",1024,1,true,true,"过期"),new("live","Protected","当前资料",".",null,null,false,false,"保护")],0,0,"只读预览",true));}
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token){Executions++;Submitted=executionId;throw new IOException("响应中断");}
        public Task<DataCleanupStatus> StatusAsync(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>Task.FromResult(new DataCleanupStatus(id,"Completed",CRS.Timing.BeijingClock.Now,null,[],1024,1,"已完成"));
    }
    internal sealed class ProgressGateway:IDataCleanupGateway
    {
        public bool CanExecute=>true;
        private TaskCompletionSource<DataCleanupStatus> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<DataCleanupStatus>? observer;
        public Guid Id{get;private set;}
        public DataCleanupStatus? Result{get;set;}
        public static DataCleanupStatus Ready(Guid id)=>new(id,"Ready",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now.AddMinutes(15),
            [new("old","UpgradeBackup","合成旧副本","maintenance/scan-fixture",20*1024*1024,20,true,true,"合成布局与进度验证")],0,0,"合成预览完成",true);
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)
            =>Begin(Guid.NewGuid(),progress,token);
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token)
            =>Begin(executionId,progress,token);
        private Task<DataCleanupStatus> Begin(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)
        {Id=id;observer=progress;completion=new(TaskCreationOptions.RunContinuationsAsynchronously);return completion.Task.WaitAsync(token);}
        public Task<DataCleanupStatus> StatusAsync(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>Task.FromResult(Result??Ready(id));
        public void Report(DataCleanupStatus status)=>observer?.Report(status);
        public void Complete(DataCleanupStatus status)=>completion.SetResult(status);
    }
    public static async Task RunAsync()
    {
        var passed=0;void Check(bool result,string name){if(!result)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var unavailable=new DataCleanupViewModel(null);Check(!unavailable.ScanCommand.CanExecute(null)&&!unavailable.ExecuteCommand.CanExecute(null),"无后台入口不能清理");
        var gateway=new Gateway();var model=new DataCleanupViewModel(gateway);Check(!model.ExecuteCommand.CanExecute(null),"扫描前禁止执行");
        await model.ScanCommand.ExecuteAsync(null);Check(gateway.Executions==0&&model.ExecuteCommand.CanExecute(null),"扫描不提交删除，预览后才允许执行");
        Check(gateway.Options is{HistoricalBackupsToKeep:0,IncludeHistoricalBackups:true},"手动扫描默认包含旧升级副本，只留最近可用回退快照");
        Check(model.Entries.Single(x=>x.Entry.Id=="live").Size=="未统计","受保护资料未知大小不显示为零");
        model.IncludeBackups=false;Check(!model.ExecuteCommand.CanExecute(null)&&model.Entries.Count==0,"修改范围使旧预览失效");
        await model.ScanCommand.ExecuteAsync(null);await model.ExecuteCommand.ExecuteAsync(null);
        Check(gateway.Executions==1&&gateway.Submitted!=Guid.Empty&&!model.ExecuteCommand.CanExecute(null),"清理响应未知时不允许重复提交");
        Check(model.CheckResultCommand.CanExecute(null)&&!model.ScanCommand.CanExecute(null),"结果未知时保留编号并提供结果查询");
        await model.CheckResultCommand.ExecuteAsync(null);Check(gateway.Executions==1&&model.ScanCommand.CanExecute(null)&&model.Summary.Contains("清理完成"),"查询恢复完成状态，不发起第二次清理");
        await VerifyProgressAsync(Check);
        Console.WriteLine($"资料清理界面模型验证通过：{passed} 项。");
    }
    private static async Task VerifyProgressAsync(Action<bool,string> check)
    {
        var gateway=new ProgressGateway();var model=new DataCleanupViewModel(gateway);
        var scan=model.ScanCommand.ExecuteAsync(null);
        check(model.Busy&&model.ProgressIndeterminate&&model.ElapsedText.Length>0&&!model.Summary.Contains("已清理"),"扫描受理前立即显示活动进度与用时，不显示删除零个");
        var scanning=ProgressGateway.Ready(gateway.Id) with{State="Scanning",CanExecute=false,Entries=[],Message="正在校验合成文件",
            Progress=new("VerifyingBackups","maintenance/scan-fixture/large.db",1234,8*1024*1024,2,1024*1024)};
        gateway.Report(scanning);await Until(()=>model.CurrentItem.Contains("scan-fixture")&&model.VerificationText.Contains("1.0 MB"));
        check(model.StopWaitingCommand.CanExecute(null)&&!model.CheckResultCommand.CanExecute(null),"后台已受理后可主动停止等待，查询不与当前跟踪重叠");
        check(model.Summary.Contains("已扫描")&&model.Summary.Contains(1234.ToString("N0"))&&model.VerificationText.Contains("1.0 MB")&&model.ProgressIndeterminate,"扫描和大文件校验持续呈现后台实际计数与当前文件，不虚构百分比");
        gateway.Complete(ProgressGateway.Ready(gateway.Id));await scan;
        gateway.Report(scanning);await Task.Delay(30);
        check(!model.Busy&&model.Summary.Contains("本次将清理")&&model.CurrentItem.Length==0,"结束后收起活动状态，迟到进度不能覆盖最终预览");
        scan=model.ScanCommand.ExecuteAsync(null);gateway.Complete(scanning with{OperationId=gateway.Id});await scan;
        check(!model.Busy&&model.CheckResultCommand.CanExecute(null)&&!model.ScanCommand.CanExecute(null),"扫描超出单次等待后保留操作编号，允许续查并阻止重复扫描");
        await model.CheckResultCommand.ExecuteAsync(null);
        check(model.ExecuteCommand.CanExecute(null),"续查扫描结果后可按预览清理");
        var execution=model.ExecuteCommand.ExecuteAsync(null);
        var running=ProgressGateway.Ready(gateway.Id) with{State="Running",CanExecute=false,Progress=new("VerifyingCleanup","maintenance/scan-fixture/large.db",VerifiedBytes:1024)};
        gateway.Report(running);await Until(()=>model.CurrentItem.Length>0);
        check(model.ProgressIndeterminate,"删除前的校验阶段保持活动进度");
        running=running with{DeletedFiles=5,DeletedBytes=5*1024*1024,Progress=running.Progress! with{Phase="Deleting"}};
        gateway.Report(running);await Until(()=>model.ProgressPercent==25&&model.Summary.Contains("共 20 个文件"));
        check(!model.ProgressIndeterminate&&model.Summary.Contains("共 20 个文件"),"清理阶段按实际已删文件数与预览总数显示确定进度");
        gateway.Complete(running with{State="Completed",DeletedFiles=20,DeletedBytes=20*1024*1024});await execution;
        check(!model.Busy&&model.ProgressPercent==100&&model.ScanCommand.CanExecute(null),"清理完成后收起活动状态并恢复操作");
        scan=model.ScanCommand.ExecuteAsync(null);gateway.Report(scanning with{OperationId=gateway.Id});await Until(()=>model.StopWaitingCommand.CanExecute(null));
        model.StopWaitingCommand.Execute(null);await scan;
        check(!model.Busy&&model.CheckResultCommand.CanExecute(null)&&!model.ScanCommand.CanExecute(null)&&!model.StopWaitingCommand.CanExecute(null),"主动停止等待保留扫描编号，可续查且不能重复扫描");
        await model.CheckResultCommand.ExecuteAsync(null);
        check(model.ExecuteCommand.CanExecute(null),"主动停止等待后查询同一后台操作仍可取得预览");
    }
    private static async Task Until(Func<bool> condition)
    {
        var until=DateTimeOffset.UtcNow.AddSeconds(3);
        while(!condition()){if(DateTimeOffset.UtcNow>=until)throw new TimeoutException("界面未收到清理进度。");await Task.Delay(10);}
    }
}
