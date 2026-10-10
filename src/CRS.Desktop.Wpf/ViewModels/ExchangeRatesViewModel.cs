using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CRS.DesktopClient.ViewModels;

/// <summary>展示年度汇率与来源状态，配置读取和校验由后台负责。</summary>
public partial class ExchangeRatesViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    [ObservableProperty] private int year;
    // 页面内保留汇率读取提示，避免空列表看起来像按钮没有响应。
    [ObservableProperty] private string rateStatus = "请选择年度，点击“读取年度汇率”。";
    public ObservableCollection<AppliedRate> Rates { get; } = [];
    public ExchangeRatesViewModel(WorkspaceState state)
    {
        State = state; year = state.Year;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.IsBusy)) { RefreshCommand.NotifyCanExecuteChanged(); OpenConfigurationCommand.NotifyCanExecuteChanged(); } };
    }
    /// <summary>切换年度后清除旧汇率，并提示重新读取。</summary>
    partial void OnYearChanged(int value) { Rates.Clear(); RateStatus = $"已选择 {value} 年，请点击“读取年度汇率”。"; }
    private bool CanOperate() => State.IsIdle;
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RefreshAsync()
    {
        // 固定本次请求年度，防止等待后台期间发生年度变化后显示错年的汇率。
        var requestedYear = Year;
        var success = await State.RunAsync(async () =>
        {
            RateStatus = State.Status = $"正在读取 {requestedYear} 年汇率……";
            var rows = await State.UseCases.RatesAsync(requestedYear);
            if (Year != requestedYear) return;
            Rates.Clear(); foreach (var row in rows) Rates.Add(row);
            RateStatus = State.Status = rows.Count == 0
                ? $"{requestedYear} 年尚未配置汇率。请打开配置目录，补充该年度汇率及来源后重新读取。"
                : $"{requestedYear} 年已读取 {rows.Count} 个币种的汇率和来源；修改后请重新计算当前报表。";
        });
        // 后台统一处理异常；页面同步显示失败原因，不停留在“正在读取”。
        if (!success && Year == requestedYear) RateStatus = State.Status;
    }
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task OpenConfigurationAsync() => await State.RunAsync(() =>
    {
        State.Interaction.OpenFolder(State.UseCases.ConfigPath); return Task.CompletedTask;
    });
}
