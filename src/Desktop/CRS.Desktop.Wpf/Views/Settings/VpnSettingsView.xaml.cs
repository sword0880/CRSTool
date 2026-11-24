using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Views.Settings;

public partial class VpnSettingsView : UserControl
{
    private readonly BackgroundPipeClient client;
    private CancellationTokenSource? operation;
    private bool addressLoaded;

    public VpnSettingsView() : this(new BackgroundPipeClient()) { }

    internal VpnSettingsView(BackgroundPipeClient client)
    {
        this.client = client;
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !addressLoaded)
                await RunAsync(LoadAsync);
        };
        Unloaded += (_, _) => ClearInputs();
    }

    private async Task LoadAsync(CancellationToken token)
    {
        var result = await WorkerVpnClient.ReadWithAddressAsync(client, token);
        token.ThrowIfCancellationRequested();
        SubscriptionInput.Text = result.SubscriptionAddress ?? "";
        addressLoaded = true;
        await LoadNodesAsync(result, token);
    }

    private async Task LoadNodesAsync(VpnSettingsStatus result, CancellationToken token)
    {
        ShowResult(result);
        if (result.Configured && result.EngineAvailable)
            await RefreshNodesAsync(token);
    }

    internal void ClearInputs()
    {
        operation?.Cancel();
        SubscriptionInput.Clear();
        addressLoaded = false;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var address = SubscriptionInput.Text.Trim();
        if (address.Length == 0)
        {
            Status.Text = "请填写 HTTPS 订阅地址。";
            SubscriptionInput.Focus();
            return;
        }
        await RunAsync(async token =>
        {
            var saved = await WorkerVpnClient.SaveAsync(client, address, token);
            token.ThrowIfCancellationRequested();
            SubscriptionInput.Text = address;
            addressLoaded = true;
            await LoadNodesAsync(saved, token);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await RunAsync(addressLoaded ? RefreshNodesAsync : LoadAsync);

    private async Task RefreshNodesAsync(CancellationToken token)
    {
        Status.Text = "正在加载节点并测速…";
        var result = await WorkerVpnClient.RefreshAsync(client, token);
        token.ThrowIfCancellationRequested();
        ShowResult(result);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("清除订阅会断开当前 VPN 连接。确定清除？", "VPN 订阅", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(async token =>
        {
            var result = await WorkerVpnClient.SaveAsync(client, "", token);
            token.ThrowIfCancellationRequested();
            SubscriptionInput.Clear();
            addressLoaded = true;
            ShowResult(result);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        SetBusy(true);
        Status.Text = "正在连接后台服务…";
        try
        {
            await action(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is BackgroundServiceException or InvalidOperationException)
        {
            if (!cancellation.IsCancellationRequested)
                Status.Text = error.Message;
        }
        finally
        {
            operation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SubscriptionInput.IsEnabled = !busy;
        SubscriptionActions.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowResult(VpnSettingsStatus result)
    {
        SubscriptionStatus.Text = result.Configured
            ? "已保存订阅。输入新地址可替换。"
            : "尚未配置订阅。保存订阅需要 Windows 管理员权限。";
        var delays = result.Delays?.Length == result.Nodes.Length ? result.Delays : null;
        var rows = result.Nodes.Select((name, index) =>
        {
            var delay = delays?[index];
            if (delay is > 0 and < 65535)
                return new NodeRow(name, delay, $"{delay} ms", delay switch
                {
                    <= 150 => LatencyLevel.Low,
                    <= 300 => LatencyLevel.Medium,
                    _ => LatencyLevel.High
                });
            return new NodeRow(name, null, delays is null ? "未测速" : "未测得", LatencyLevel.Unknown);
        }).OrderBy(row => row.Delay ?? int.MaxValue).ToArray();

        NodeList.ItemsSource = rows;
        NodeCount.Text = $"{rows.Length} 个";
        NodeCountBadge.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = !result.EngineAvailable ? "后台服务未安装 Mihomo。" :
            !result.Configured ? "保存订阅后会自动加载节点并测速。" :
            rows.Length == 0 ? "暂未读取到节点，请刷新重试。" :
            delays is null ? "暂无延迟数据，可刷新节点重新测速。" : "";
    }

    private enum LatencyLevel { Unknown, Low, Medium, High }

    private sealed record NodeRow(string Name, int? Delay, string Latency, LatencyLevel Level);
}
