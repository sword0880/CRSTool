using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>展示年度汇率与来源状态，配置读取和校验由后台负责。</summary>
public partial class ExchangeRatesViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    [ObservableProperty] private int year;
    public ObservableCollection<AppliedRate> Rates { get; } = [];
    public ExchangeRatesViewModel(WorkspaceState state)
    {
        State = state; year = state.Year;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) { RefreshCommand.NotifyCanExecuteChanged(); OpenConfigurationCommand.NotifyCanExecuteChanged(); } };
    }
    partial void OnYearChanged(int value) { Rates.Clear(); }
    private bool CanOperate() => State.IsIdle;
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshAsync() => await State.RunAsync(async () =>
    {
        var rows = await State.UseCases.RatesAsync(Year); Rates.Clear(); foreach (var row in rows) Rates.Add(row);
        State.Status = rows.Count == 0 ? $"{Year} 年尚未配置汇率。" : $"{Year} 年汇率和来源已读取；修改后请重新计算当前报表。";
    });
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task OpenConfigurationAsync() => await State.RunAsync(() =>
    {
        State.Interaction.OpenFolder(State.UseCases.ConfigPath); return Task.CompletedTask;
    });
}
