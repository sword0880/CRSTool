using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>管理当前快照的人工核对依据。</summary>
public partial class ReviewViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    [ObservableProperty] private string category = "资料核对";
    [ObservableProperty] private string evidence = "";
    [ObservableProperty] private bool confirmed;
    public ReviewViewModel(WorkspaceState state)
    {
        State = state;
        state.ResultChanged += () => { Evidence = ""; Confirmed = false; SaveCommand.NotifyCanExecuteChanged(); };
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) SaveCommand.NotifyCanExecuteChanged(); };
    }
    private bool CanSave() => State.IsIdle && State.HasResult;
    /// <summary>明确确认后保存，保持原始税额和问题状态。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!Confirmed) { State.Interaction.ShowError("请先勾选复核确认。"); return; }
        var current = State.Result!;
        await State.RunAsync(async () =>
        {
            await State.UseCases.SaveReviewAsync(current.SnapshotId, Category, Evidence.Trim());
            await State.DisplayAsync(current, State.Fingerprint); State.Status = "人工复核依据已保存，原始核算状态保持不变。";
        });
    }
}
