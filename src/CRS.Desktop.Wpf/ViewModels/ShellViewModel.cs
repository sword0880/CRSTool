using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

/// <summary>装配页面模型与共享会话，导航事件不包含业务规则。</summary>
public sealed class ShellViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    /// <summary>独立 Worker 的只读全局状态，不覆盖业务页面操作提示。</summary>
    public ShellStatusBarViewModel StatusBar { get; } = new();
    public ImportViewModel Import { get; }
    public ResultsViewModel Results { get; }
    public HistoryViewModel History { get; }
    public DashboardViewModel Dashboard { get; }
    public ReviewViewModel Review { get; }
    public ReportsViewModel Reports { get; }
    public ExchangeRatesViewModel ExchangeRates { get; }
    public SettingsViewModel? Settings { get; }
    public PerformanceViewModel Performance {get;}
    public event Action<string>? NavigateRequested;
    public ShellViewModel(IDesktopUseCases useCases, IUserInteraction interaction, IDesktopSettingsService? settings = null,IVaultService? vault=null,CRS.Application.Performance.IPerformanceUseCases? performance=null,Func<string>? documentPassword=null,IDataCleanupGateway? cleanup=null)
    {
        State = new(useCases, interaction,documentPassword ?? (vault is null?null:()=>vault.DocumentEncryptionPassword)); Results = new(State); History = new(State, () => NavigateRequested?.Invoke("tax"));
        Dashboard = new(State, History, page => NavigateRequested?.Invoke(page));
        Review = new(State); Reports = new(State); ExchangeRates = new(State);
        Settings = settings is null ? null : new(State, settings,vault,cleanup);
        Performance=new(State,performance);
        Import = new(State, () => { NavigateRequested?.Invoke("tax"); _ = History.RefreshCommand.ExecuteAsync(null); });
    }
}
