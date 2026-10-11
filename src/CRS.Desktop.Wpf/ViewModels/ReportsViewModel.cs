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
    private void Refresh() { ExportCommand.NotifyCanExecuteChanged(); ExportCarryCommand.NotifyCanExecuteChanged(); }
    private bool CanExport() => State.IsIdle && State.HasResult;
    private bool CanCarry() => CanExport() && State.Result!.CarryEligible;
    // 取消命令只作用于本次导出，后台同时关联工作区锁定信号。
    [RelayCommand(CanExecute = nameof(CanExport),IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken cancellation)
    {
        if(!State.Interaction.ConfirmPlaintextExport()) {State.Status="已取消明文导出。"; return;}
        var result = State.Result!;
        var path = State.Interaction.SaveFile("Excel 底稿|*.xlsx", $"{(result.Complete ? "Tax_Report" : "Partial_Review")}_{result.Year}.xlsx");
        if (path is null) return;
        await State.RunAsync(async () => { await State.UseCases.ExportAsync(path, result, State.Fingerprint,cancellation); State.Status = "Excel 底稿已保存。"; });
    }
    [RelayCommand(CanExecute = nameof(CanCarry),IncludeCancelCommand = true)]
    private async Task ExportCarryAsync(CancellationToken cancellation)
    {
        if(!State.Interaction.ConfirmPlaintextExport()) {State.Status="已取消明文导出。"; return;}
        var result = State.Result!;
        var path = State.Interaction.SaveFile("C# LOT 结转|*.json", $"LOTS_{result.Year}.json");
        if (path is null) return;
        await State.RunAsync(async () => { await State.UseCases.ExportCarryAsync(path, result,cancellation); State.Status = "年末 LOT 结转已保存。"; });
    }
}
