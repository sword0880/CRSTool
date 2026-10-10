using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

/// <summary>装配页面模型与共享会话，导航事件不包含业务规则。</summary>
public sealed class ShellViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public ImportViewModel Import { get; }
    public ResultsViewModel Results { get; }
    public HistoryViewModel History { get; }
    public DashboardViewModel Dashboard { get; }
    public ReviewViewModel Review { get; }
    public ReportsViewModel Reports { get; }
    public ExchangeRatesViewModel ExchangeRates { get; }
    public SettingsViewModel? Settings { get; }
    public event Action<string>? NavigateRequested;
    public ShellViewModel(IDesktopUseCases useCases, IUserInteraction interaction, IDesktopSettingsService? settings = null)
    {
        State = new(useCases, interaction); Results = new(State); History = new(State);
        Dashboard = new(State, History, page => NavigateRequested?.Invoke(page));
        Review = new(State); Reports = new(State); ExchangeRates = new(State);
        Settings = settings is null ? null : new(State, settings);
        Import = new(State, () => { NavigateRequested?.Invoke("tax"); _ = History.RefreshCommand.ExecuteAsync(null); });
    }
}
