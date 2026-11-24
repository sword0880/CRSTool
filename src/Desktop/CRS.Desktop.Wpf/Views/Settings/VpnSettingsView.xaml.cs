using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Views.Settings;

public partial class VpnSettingsView : UserControl
{
    private readonly BackgroundPipeClient client = new();
    private CancellationTokenSource? operation;
    private bool addressLoaded;

    public VpnSettingsView()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !addressLoaded)
                await RunAsync(async token =>
                {
                    var result = await WorkerVpnClient.ReadWithAddressAsync(client, token);
                    token.ThrowIfCancellationRequested();
                    SubscriptionInput.Text = result.SubscriptionAddress ?? "";
                    addressLoaded = true;
                    return result;
                });
        };
        Unloaded += (_, _) => ClearInputs();
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
        if (address.Length == 0) { Status.Text = "请填写 HTTPS 订阅地址。"; return; }
        await RunAsync(async token =>
        {
            var saved = await WorkerVpnClient.SaveAsync(client, address, token);
            token.ThrowIfCancellationRequested();
            SubscriptionInput.Text = address;
            addressLoaded = true;
            return saved;
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await RunAsync(token => WorkerVpnClient.RefreshAsync(client, token));

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("清除订阅会断开当前 VPN 连接。确定清除？", "VPN 订阅", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(async token =>
        {
            var result = await WorkerVpnClient.SaveAsync(client, "", token);
            token.ThrowIfCancellationRequested();
            SubscriptionInput.Clear();
            return result;
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task<VpnSettingsStatus>> action)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        Form.IsEnabled = false;
        Status.Text = "正在连接后台服务…";
        try
        {
            var result = await action(cancellation.Token);
            SubscriptionStatus.Text = result.Configured ? "已保存订阅。输入新地址可替换。" : "尚未配置订阅。";
            NodeList.ItemsSource = result.Nodes.Select(result.NodeLabel).ToArray();
            Status.Text = !result.EngineAvailable ? "后台服务未安装 Mihomo。保存或替换订阅需要 Windows 管理员权限。" :
                result.Nodes.Length > 0 ? $"已读取 {result.Nodes.Length} 个节点。" :
                result.Configured ? "点击“刷新节点”读取订阅。" : "保存订阅需要 Windows 管理员权限。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is BackgroundServiceException or InvalidOperationException)
        { Status.Text = error.Message; }
        finally { operation = null; Form.IsEnabled = true; }
    }
}
