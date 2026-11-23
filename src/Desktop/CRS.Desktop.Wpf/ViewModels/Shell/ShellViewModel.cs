using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Application.Abstractions;
using CRS.AI.Models;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels.AI;

namespace CRS.DesktopClient.ViewModels;

/// <summary>装配页面模型与共享会话，导航事件不包含业务规则。</summary>
public sealed class ShellViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    /// <summary>独立 Worker 的只读全局状态，不覆盖业务页面操作提示。</summary>
    public ShellStatusBarViewModel StatusBar { get; }
    internal CRS.Worker.Ipc.BackgroundPipeClient WorkerClient { get; }
    public ImportViewModel Import { get; }
    public ResultsViewModel Results { get; }
    public HistoryViewModel History { get; }
    public DashboardViewModel Dashboard { get; }
    public ReviewViewModel Review { get; }
    public ReportsViewModel Reports { get; }
    public ExchangeRatesViewModel ExchangeRates { get; }
    public SettingsViewModel? Settings { get; }
    public AiAssistantViewModel AiAssistant { get; }
    public AiChatHistoryViewModel AiHistory { get; }
    public PerformanceViewModel Performance {get;}
    public event Action<string>? NavigateRequested;
    public ShellViewModel(IDesktopUseCases useCases, IUserInteraction interaction, IDesktopSettingsService? settings = null,IVaultService? vault=null,CRS.Application.Performance.IPerformanceUseCases? performance=null,Func<string>? documentPassword=null,IDataCleanupGateway? cleanup=null,CRS.Worker.Ipc.BackgroundPipeClient? workerClient=null,
        IAiModelService? aiModels=null, IAiChatHistory? aiHistory=null)
    {
        State = new(useCases, interaction,documentPassword ?? (vault is null?null:()=>vault.DocumentEncryptionPassword)); Results = new(State);
        History = new(State, () => NavigateRequested?.Invoke("tax"), () => NavigateRequested?.Invoke("import"));
        Performance=new(State,performance);
        Review = new(State); Reports = new(State); ExchangeRates = new(State);
        // 问答与设置共用同一模型服务，每次发送时读取已保存的默认配置。
        Settings = settings is null ? null : new(State, settings,vault,cleanup,aiModels);
        AiAssistant = new(aiModels, Settings?.AiModels ?? new(aiModels), aiHistory);
        AiHistory = new(aiHistory);
        WorkerClient = workerClient ?? new();
        StatusBar = new(WorkerClient, Settings?.Cleanup);
        Import = new(State, () => { NavigateRequested?.Invoke("tax"); _ = History.RefreshCommand.ExecuteAsync(null); });
        Dashboard = new(State, Performance, settings, page => NavigateRequested?.Invoke(page), year =>
        {
            Import.Year = year;
            NavigateRequested?.Invoke("import");
        });
        History.ResultOpened += Dashboard.ShowOpenedHistory;
    }
}
