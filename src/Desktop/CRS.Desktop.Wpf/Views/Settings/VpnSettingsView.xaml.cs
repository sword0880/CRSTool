using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Views.Settings;

public partial class VpnSettingsView : UserControl
{
    private readonly BackgroundPipeClient client;
    private CancellationTokenSource? operation;
    private bool addressLoaded;
    private NodeCard[] cards = [];

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
        var types = result.NodeTypes?.Length == result.Nodes.Length ? result.NodeTypes : null;
        var details = (result.SubscriptionDetails ?? []).ToList();
        var nodes = new List<NodeCard>();
        for (var index = 0; index < result.Nodes.Length; index++)
        {
            var name = result.Nodes[index];
            // 旧版后台仍可能把套餐信息放在节点数组里。
            if (VpnSubscriptionDetail.Parse(name) is { } detail)
            {
                details.Add(detail);
                continue;
            }
            var delay = delays?[index];
            if (delay is not (> 0 and < 65535)) delay = null;
            var level = delay switch
            {
                null => LatencyLevel.Unknown,
                <= 150 => LatencyLevel.Low,
                <= 300 => LatencyLevel.Medium,
                _ => LatencyLevel.High
            };
            var latency = delay is not null ? $"{delay} ms" : delays is not null ? "未测得" :
                result.LatencyTestFailed ? "测速失败" : "未测速";
            nodes.Add(new NodeCard(name, types?[index] ?? "", GetRegion(name), delay, latency, level));
        }

        var plan = details.DistinctBy(detail => detail.Label).OrderBy(detail => detail.Label switch
        {
            "剩余流量" => 0,
            "套餐到期" => 1,
            _ => 2
        }).ToArray();
        SubscriptionDetails.ItemsSource = plan;
        SubscriptionDetails.Visibility = plan.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowCards(nodes.OrderBy(card => card.Delay ?? int.MaxValue).ToArray());
        Status.Text = !result.EngineAvailable ? "后台服务未安装 Mihomo。" :
            !result.Configured ? "保存订阅后会自动加载节点并测速。" :
            cards.Length == 0 ? "暂未读取到节点，请刷新重试。" :
            result.LatencyTestFailed ? "节点已加载，本次测速失败。可刷新节点重试。" :
            delays is null ? "后台未返回测速数据。请核对桌面端和 Worker 是否为配套版本。" :
            cards.All(card => card.Delay is null) ? "测速已完成，暂未测得可用延迟。" : "";
    }

    private void ShowCards(NodeCard[] nodes)
    {
        var previousRegion = Regions.SelectedItem as string;
        cards = nodes;
        var regions = nodes.Select(card => card.Region).Distinct()
            .OrderBy(region => region == "其他" ? 1 : 0).ThenBy(region => region).ToArray();
        Regions.ItemsSource = new[] { "全部" }.Concat(regions);
        Regions.SelectedItem = regions.Contains(previousRegion) ? previousRegion : "全部";
        UpdateVisibleCards();
        Regions.Visibility = regions.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        NodeCount.Text = $"{cards.Length} 个";
        NodeCountBadge.Visibility = cards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeList.Visibility = cards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRegionSelected(object sender, SelectionChangedEventArgs e) => UpdateVisibleCards();

    private void UpdateVisibleCards() =>
        NodeList.ItemsSource = Regions.SelectedItem is string region && region != "全部"
            ? cards.Where(card => card.Region == region).ToArray() : cards;

    private void OnNodeListSizeChanged(object sender, SizeChangedEventArgs e) =>
        NodeList.Tag = Math.Max(1, (int)(e.NewSize.Width / 170));

    private static readonly (string Region, Regex Pattern)[] RegionPatterns =
        new (string Region, string Pattern)[]
        {
            ("香港", @"🇭🇰|香港|Hong ?Kong|^HK(?![a-z])|\bHK\b"),
            ("台湾", @"🇹🇼|台湾|臺灣|Taiwan|^TW(?![a-z])|\bTW\b"),
            ("日本", @"🇯🇵|日本|东京|東京|大阪|Japan|Tokyo|^JP(?=AWS|[^a-z]|$)|\bJP\b"),
            ("新加坡", @"🇸🇬|新加坡|狮城|獅城|Singapore|^SG(?![a-z])|\bSG\b"),
            ("韩国", @"🇰🇷|韩国|韓國|首尔|Korea|Seoul|^KR(?![a-z])|\bKR\b"),
            ("美国", @"🇺🇸|美国|美國|洛杉矶|纽约|Los Angeles|New York|^USA?(?![a-z])|\bUSA?\b"),
            ("英国", @"🇬🇧|英国|英國|伦敦|London|\bUK\b"),
            ("德国", @"🇩🇪|德国|德國|法兰克福|Germany|Frankfurt"),
            ("法国", @"🇫🇷|法国|法國|巴黎|France|Paris"),
            ("加拿大", @"🇨🇦|加拿大|Canada"),
            ("澳大利亚", @"🇦🇺|澳大利[亚亞]|澳洲|悉尼|Australia|Sydney")
        }.Select(entry => (entry.Region, new Regex(entry.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToArray();

    private static string GetRegion(string name) =>
        RegionPatterns.FirstOrDefault(entry => entry.Pattern.IsMatch(name)).Region ?? "其他";

    private enum LatencyLevel { Unknown, Low, Medium, High }

    private sealed record NodeCard(string Name, string Protocol, string Region, int? Delay, string Latency, LatencyLevel Level);
}
