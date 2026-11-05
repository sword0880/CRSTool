using CRS.Application;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

/// <summary>原生加密导出使用统一密码，验证缺配置拒绝、格式传递及两种取消入口。</summary>
internal static class PasswordReportsInteractionChecks
{
    public static async Task RunAsync()
    {
        var cases=new Cases(); var interaction=new Interaction(); var password="";
        var shell=new ShellViewModel(cases,interaction,documentPassword:()=>password);var model=shell.Reports;
        if(model.ExportProtectedExcelCommand.CanExecute(null)||model.ExportProtectedPdfCommand.CanExecute(null)) throw new Exception("无结果仍可导出报税文档。");
        await shell.State.DisplayAsync(cases.Result);
        await model.ExportProtectedExcelCommand.ExecuteAsync(null); await model.ExportProtectedPdfCommand.ExecuteAsync(null);
        if(cases.Calls!=0 || interaction.Saves!=0 || !shell.State.Status.Contains("文档加密")) throw new Exception("缺密码未拒绝或打开文件对话框。");
        password="Synthetic-Document-Password";
        foreach(var format in new[]{TaxDocumentFormat.Excel,TaxDocumentFormat.Pdf})
        {
            var command=format==TaxDocumentFormat.Excel?model.ExportProtectedExcelCommand:model.ExportProtectedPdfCommand;
            var cancel=format==TaxDocumentFormat.Excel?model.ExportProtectedExcelCancelCommand:model.ExportProtectedPdfCancelCommand;
            var task=command.ExecuteAsync(null);
            if(!shell.State.IsBusy || !cancel.CanExecute(null) || model.ExportEncryptedCommand.CanExecute(null)
                || cases.Format!=format || cases.Password!=password || !interaction.LastName.EndsWith(format==TaxDocumentFormat.Excel?".xlsx":".pdf")) throw new Exception("原生加密格式／密码传递或忙碌门槛错误。");
            cancel.Execute(null);await task;
            if(!cases.Cancelled || shell.State.IsBusy || !shell.State.HasResult || shell.State.Status.Contains("已保存")) throw new Exception("取消未到后台或误报成功。");
        }
        Console.WriteLine("加密 Excel／PDF 交互通过：统一设置、空结果／缺密码拒绝、格式／扩展名、并发门槛和两类取消。");
    }
    private sealed class Cases:FakeUseCases
    {
        public int Calls;public bool Cancelled;public TaxDocumentFormat Format;public string Password="";
        public override async Task ExportPasswordProtectedAsync(string path,CalculationResult result,string fingerprint,TaxDocumentFormat format,string password,CancellationToken cancellation=default)
        {Calls++;Format=format;Password=password;try{await Task.Delay(Timeout.Infinite,cancellation);}finally{Cancelled=cancellation.IsCancellationRequested;}}
    }
    private sealed class Interaction:IUserInteraction
    {
        public int Saves;public string LastName="";
        public string? SaveFile(string filter,string name){Saves++;LastName=name;return name;}
        public string[] PickFiles(string filter,bool multiple)=>[];
        public bool ConfirmPlaintextExport()=>throw new Exception("加密导出不应请求明文授权。");
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path){}
    }
}
