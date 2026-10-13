using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class ReportsInteractionChecks
{
    /// <summary>取消按钮只影响当前导出，取消后保留当前结果并恢复操作资格。</summary>
    internal static async Task RunAsync()
    {
        var cases=new Cases(); var shell=new ShellViewModel(cases,new Interaction());
        await shell.State.DisplayAsync(new() {Year=2025,SnapshotId="REPORT-CANCEL",Summary=new(0,0,0,0,0,0,0),CarryEligible=true});
        if(shell.Reports.ExportCancelCommand.CanExecute(null)) throw new Exception("空闲时可取消不存在的导出。");
        var excel=shell.Reports.ExportCommand.ExecuteAsync(null);
        if(!shell.State.IsBusy || !shell.Reports.ExportCancelCommand.CanExecute(null) || shell.Reports.ExportCarryCommand.CanExecute(null)) throw new Exception("导出中忙碌或取消门槛错误。");
        shell.Reports.ExportCancelCommand.Execute(null); await excel;
        if(!cases.ExcelCancelled || shell.State.IsBusy || !shell.State.HasResult || !shell.Reports.ExportCommand.CanExecute(null)
            || shell.Reports.ExportCancelCommand.CanExecute(null) || shell.State.Status.Contains("已保存")) throw new Exception("取消 Excel 后状态未恢复或误报成功。");
        var carry=shell.Reports.ExportCarryCommand.ExecuteAsync(null);
        if(!shell.Reports.ExportCarryCancelCommand.CanExecute(null)) throw new Exception("结转导出没有取消入口。");
        shell.Reports.ExportCarryCancelCommand.Execute(null); await carry;
        if(!cases.CarryCancelled || shell.State.IsBusy || !shell.Reports.ExportCarryCommand.CanExecute(null)) throw new Exception("结转取消没有传给后台或没有恢复资格。");
        Console.WriteLine("导出交互验证通过：忙碌门槛、两类取消命令、取消不误报成功、保留结果及恢复资格。");
    }
    private sealed class Cases:FakeUseCases
    {
        public bool ExcelCancelled {get;private set;}
        public bool CarryCancelled {get;private set;}
        public override async Task ExportAsync(string path,CalculationResult result,string fingerprint,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)
        {if(consent?.PlaintextConfirmed!=true || consent.TemporaryLocationConfirmed) throw new Exception("界面未传本次明文授权或开放临时目录。"); try {await Task.Delay(Timeout.Infinite,cancellation);} finally {ExcelCancelled=cancellation.IsCancellationRequested;}}
        public override async Task ExportCarryAsync(string path,CalculationResult result,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)
        {if(consent?.PlaintextConfirmed!=true || consent.TemporaryLocationConfirmed) throw new Exception("结转未传本次明文授权。"); try {await Task.Delay(Timeout.Infinite,cancellation);} finally {CarryCancelled=cancellation.IsCancellationRequested;}}
    }
    private sealed class Interaction:IUserInteraction
    {
        public bool ConfirmPlaintextExport()=>true;
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string name)=>"synthetic-export";
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path) { }
    }
}
