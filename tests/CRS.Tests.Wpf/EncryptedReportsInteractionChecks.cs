using CRS.Application;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class EncryptedReportsInteractionChecks
{
    /// <summary>验证加密操作门槛、密码确认、取消传递及操作完成后的密码清理。</summary>
    public static async Task RunAsync()
    {
        var cases=new Cases(); var interaction=new Interaction();string configuredPassword="";var shell=new ShellViewModel(cases,interaction,documentPassword:()=>configuredPassword);
        var model=shell.Reports;
        if(model.ExportEncryptedCommand.CanExecute(null) || !model.RestoreEncryptedCommand.CanExecute(null)) throw new Exception("无结果时加密操作门槛错误。");
        await shell.State.DisplayAsync(cases.Result); var cleared=0; model.SensitiveCleared+=()=>cleared++;
        configuredPassword="";
        await model.ExportEncryptedCommand.ExecuteAsync(null);
        if(cases.Exports!=0 || cleared!=1) throw new Exception("确认不一致仍导出或密码未清理。");
        configuredPassword="Synthetic-Export-Password";
        var exporting=model.ExportEncryptedCommand.ExecuteAsync(null);
        if(!shell.State.IsBusy || !model.ExportEncryptedCancelCommand.CanExecute(null) || model.RestoreEncryptedCommand.CanExecute(null)) throw new Exception("加密忙碌门槛错误。");
        model.ExportEncryptedCancelCommand.Execute(null); await exporting;
        if(!cases.Cancelled || shell.State.IsBusy || cleared!=2 || !shell.State.HasResult) throw new Exception("加密取消或密码清理错误。");
        // 配置缺失时不调用后台，明确提示先到设置配置。
        configuredPassword="";
        await model.ExportEncryptedCommand.ExecuteAsync(null);
        if(cases.Exports!=1) throw new Exception("完成操作后仍沿用密码。");
        configuredPassword="Synthetic-Export-Password";
        await model.RestoreEncryptedCommand.ExecuteAsync(null);
        if(cases.Restores!=1 || !interaction.RestoreConfirmed || shell.State.IsBusy) throw new Exception("解密未确认明文恢复或未调用后台。");
        configuredPassword="";model.ClearSensitiveInputs();
        await model.RestoreEncryptedCommand.ExecuteAsync(null);
        if(cases.Restores!=1) throw new Exception("锁定清理后仍能沿用密码恢复。");
        Console.WriteLine("加密报表交互通过：无结果恢复、密码确认、忙碌／取消、明文恢复确认及密码清理。");
    }
    private sealed class Cases:FakeUseCases
    {
        public int Exports,Restores; public bool Cancelled;
        public override async Task ExportEncryptedAsync(string path,CalculationResult result,string fingerprint,string password,CancellationToken cancellation=default)
        {Exports++; try {await Task.Delay(Timeout.Infinite,cancellation);} finally {Cancelled=cancellation.IsCancellationRequested;}}
        public override Task<string> RestoreEncryptedAsync(string path,string parentDirectory,string password,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)
        {if(consent?.PlaintextConfirmed!=true || consent.TemporaryLocationConfirmed) throw new Exception("解密恢复未传明文授权。"); Restores++; return Task.FromResult("synthetic-restored");}
    }
    private sealed class Interaction:IUserInteraction
    {
        public bool RestoreConfirmed;
        public string[] PickFiles(string filter,bool multiple)=>["synthetic.crsexport"];
        public string? SaveFile(string filter,string name)=>"synthetic.crsexport";
        public string? PickFolder()=>"synthetic-parent";
        public bool ConfirmPlaintextRestore() {RestoreConfirmed=true; return true;}
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path) { }
    }
}
