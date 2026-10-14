using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>选择报告输出位置，后台执行导出门槛校验及写入。</summary>
public partial class ReportsViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public ReportsViewModel(WorkspaceState state)
    {
        State = state; state.ResultChanged += Refresh;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) Refresh(); };
    }
    private void Refresh() { ExportCommand.NotifyCanExecuteChanged(); ExportCarryCommand.NotifyCanExecuteChanged(); ExportEncryptedCommand.NotifyCanExecuteChanged(); RestoreEncryptedCommand.NotifyCanExecuteChanged(); }
    private bool CanExport() => State.IsIdle && State.HasResult;
    private bool CanCarry() => CanExport() && State.Result!.CarryEligible;
    private bool CanRestore()=>State.IsIdle;
    public event Action? SensitiveCleared;
    public void ClearSensitiveInputs()=>SensitiveCleared?.Invoke();
    [RelayCommand(CanExecute=nameof(CanExport),IncludeCancelCommand=true)]
    private async Task ExportEncryptedAsync(CancellationToken cancellation)
    {
        try
        {
            var password=State.DocumentEncryptionPassword;
            if(password.Length is <12 or >1024) {State.Status="请先在设置中统一配置文档加密密码。";return;}
            var result=State.Result!;
            var path=State.Interaction.SaveFile("加密底稿|*.crsexport","Protected_Report.crsexport");
            if(path is null) return;
            var secret=password;
            await State.RunAsync(async()=> {await State.UseCases.ExportEncryptedAsync(path,result,State.Fingerprint,secret,cancellation); State.Status="加密底稿已保存，请妥善保管独立导出密码。";});
        }
        finally {ClearSensitiveInputs();}
    }
    [RelayCommand(CanExecute=nameof(CanRestore),IncludeCancelCommand=true)]
    private async Task RestoreEncryptedAsync(CancellationToken cancellation)
    {
        try
        {
            var password=State.DocumentEncryptionPassword;
            if(password.Length is <12 or >1024) {State.Status="请先在设置中配置此文件使用的文档加密密码。";return;}
            var files=State.Interaction.PickFiles("加密底稿|*.crsexport",false);
            if(files.Length!=1 || !State.Interaction.ConfirmPlaintextRestore()) return;
            var parent=State.Interaction.PickFolder(); if(parent is null) return;
            var secret=password;
            await State.RunAsync(async()=> {var restored=await State.UseCases.RestoreEncryptedAsync(files[0],parent,secret,cancellation,new(true)); State.Status="已恢复到新文件夹："+restored;});
        }
        finally {ClearSensitiveInputs();}
    }
    // 取消命令只作用于本次导出，后台同时关联工作区锁定信号。
    [RelayCommand(CanExecute = nameof(CanExport),IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken cancellation)
    {
        if(!State.Interaction.ConfirmPlaintextExport()) {State.Status="已取消明文导出。"; return;}
        var result = State.Result!;
        var path = State.Interaction.SaveFile("Excel 底稿|*.xlsx", $"{(result.Complete ? "Tax_Report" : "Partial_Review")}_{result.Year}.xlsx");
        if (path is null) return;
        await State.RunAsync(async () => { await State.UseCases.ExportAsync(path, result, State.Fingerprint,cancellation,new(true)); State.Status = "Excel 底稿已保存。"; });
    }
    [RelayCommand(CanExecute = nameof(CanCarry),IncludeCancelCommand = true)]
    private async Task ExportCarryAsync(CancellationToken cancellation)
    {
        if(!State.Interaction.ConfirmPlaintextExport()) {State.Status="已取消明文导出。"; return;}
        var result = State.Result!;
        var path = State.Interaction.SaveFile("C# LOT 结转|*.json", $"LOTS_{result.Year}.json");
        if (path is null) return;
        await State.RunAsync(async () => { await State.UseCases.ExportCarryAsync(path, result,cancellation,new(true)); State.Status = "年末 LOT 结转已保存。"; });
    }
}
