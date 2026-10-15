using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.PluginHost;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views.Pages;

/// <summary>插件状态图表与操作只投影宿主快照，启停配置仍交由宿主保存。</summary>
public partial class PluginManagementPage : UserControl
{
    public IReadOnlyList<PluginManagementRow> Rows { get; }
    public int Running { get; }
    public int Disabled { get; }
    public int Failed { get; }
    public int ChartMaximum => Math.Max(1, Rows.Count);
    public string TotalLabel => $"共 {Rows.Count} 个插件";
    public string RunningLabel => $"运行中 {Running} / {Rows.Count}";

    public PluginManagementPage(IEnumerable<PluginStatus> statuses, Action<string, bool> save, Func<string, bool> nextEnabled)
    {
        var snapshot = statuses.ToArray();
        Rows = snapshot.Select(status => new PluginManagementRow(status, save, nextEnabled(status.Id))).ToArray();
        Running = snapshot.Count(status => status.State == PluginState.Started);
        Disabled = snapshot.Count(status => status.State == PluginState.Disabled);
        Failed = snapshot.Count(status => status.State == PluginState.Failed);
        InitializeComponent(); DataContext = this;
    }
}

/// <summary>当前运行状态和下次启动设置分开显示，保存失败不伪装成操作成功。</summary>
public sealed class PluginManagementRow : ObservableObject
{
    private readonly PluginStatus status;
    private readonly Action<string, bool> save;
    private bool nextEnabled;
    private string? failure;
    public string Id => status.Id;
    public string Name => status.Name;
    public string Version => status.Version ?? "未确定";
    public string StateLabel => status.State switch
    {
        PluginState.Started => "运行中", PluginState.Disabled => "已禁用", PluginState.Failed => "异常",
        PluginState.Discovered => "已发现", PluginState.Verified => "已验证", PluginState.Loaded => "已加载",
        PluginState.Stopping => "停止中", _ => "已停止"
    };
    public string StateForeground => status.State == PluginState.Started ? "#15803D" : status.State == PluginState.Failed ? "#B45309" : "#64748B";
    public string StateBackground => status.State == PluginState.Started ? "#F0FDF4" : status.State == PluginState.Failed ? "#FFFBEB" : "#F1F5F9";
    public string NextLabel => failure ?? (nextEnabled == (status.State != PluginState.Disabled) ? nextEnabled ? "启用" : "禁用" : nextEnabled ? "待启用 · 重启后生效" : "待禁用 · 重启后生效");
    public string Detail => string.Join("\n", new[] { failure, status.Error, status.RecoveryMessage }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public string ActionLabel => nextEnabled ? "下次启动禁用" : "下次启动启用";
    public SymbolRegular ActionIcon => nextEnabled ? SymbolRegular.Pause24 : SymbolRegular.Play24;
    public string ActionForeground => nextEnabled ? "#64748B" : "#2563EB";
    public IRelayCommand ToggleCommand { get; }

    public PluginManagementRow(PluginStatus status, Action<string, bool> save, bool nextEnabled)
    {
        this.status = status; this.save = save; this.nextEnabled = nextEnabled;
        ToggleCommand = new RelayCommand(Toggle);
    }

    private void Toggle()
    {
        try { save(Id, !nextEnabled); nextEnabled = !nextEnabled; failure = null; }
        catch { failure = "保存失败，请重试"; }
        foreach (var property in new[] { nameof(NextLabel), nameof(Detail), nameof(ActionLabel), nameof(ActionIcon), nameof(ActionForeground) })
            OnPropertyChanged(property);
    }
}
