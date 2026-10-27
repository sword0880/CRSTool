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
        public bool CanExecute=>true;public int Executions;public Guid Submitted;
        public Task<DataCleanupStatus> PreviewAsync(DataCleanupOptions options,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>Task.FromResult(new DataCleanupStatus(Guid.NewGuid(),"Ready",CRS.Timing.BeijingClock.Now,CRS.Timing.BeijingClock.Now.AddMinutes(15),[new("log","Logs","旧日志","logs/old",1024,1,true,true,"过期"),new("live","Protected","当前资料",".",null,null,false,false,"保护")],0,0,"只读预览",true));
        public Task<DataCleanupStatus> ExecuteAsync(Guid previewId,Guid executionId,IProgress<DataCleanupStatus>? progress,CancellationToken token){Executions++;Submitted=executionId;throw new IOException("响应中断");}
        public Task<DataCleanupStatus> StatusAsync(Guid id,IProgress<DataCleanupStatus>? progress,CancellationToken token)=>Task.FromResult(new DataCleanupStatus(id,"Completed",CRS.Timing.BeijingClock.Now,null,[],1024,1,"已完成"));
    }
    public static async Task RunAsync()
    {
        var passed=0;void Check(bool result,string name){if(!result)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var unavailable=new DataCleanupViewModel(null);Check(!unavailable.ScanCommand.CanExecute(null)&&!unavailable.ExecuteCommand.CanExecute(null),"无后台入口不能清理");
        var gateway=new Gateway();var model=new DataCleanupViewModel(gateway);Check(!model.ExecuteCommand.CanExecute(null),"扫描前禁止执行");
        await model.ScanCommand.ExecuteAsync(null);Check(gateway.Executions==0&&model.ExecuteCommand.CanExecute(null),"扫描不提交删除，预览后才允许执行");
        Check(model.Entries.Single(x=>x.Entry.Id=="live").Size=="未统计","受保护资料未知大小不显示为零");
        model.IncludeBackups=true;Check(!model.ExecuteCommand.CanExecute(null)&&model.Entries.Count==0,"修改范围使旧预览失效");
        await model.ScanCommand.ExecuteAsync(null);await model.ExecuteCommand.ExecuteAsync(null);
        Check(gateway.Executions==1&&gateway.Submitted!=Guid.Empty&&!model.ExecuteCommand.CanExecute(null),"清理响应未知时不允许重复提交");
        Check(model.CheckResultCommand.CanExecute(null)&&!model.ScanCommand.CanExecute(null),"结果未知时保留编号并提供结果查询");
        await model.CheckResultCommand.ExecuteAsync(null);Check(gateway.Executions==1&&model.ScanCommand.CanExecute(null)&&model.Summary.Contains("清理完成"),"查询恢复完成状态，不发起第二次清理");
        Console.WriteLine($"资料清理界面模型验证通过：{passed} 项。");
    }
}
