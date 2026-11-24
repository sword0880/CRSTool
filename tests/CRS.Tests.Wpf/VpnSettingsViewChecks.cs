using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.Views.Settings;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

internal static class VpnSettingsViewChecks
{
    internal static void Run(string screenshot)
    {
        using var stop = new CancellationTokenSource();
        var fixture = new VpnFixture();
        var pipe = "CRS.VpnSettings.Tests." + Guid.NewGuid().ToString("N");
        var server = new BackgroundPipeServer(fixture, pipe).RunAsync(stop.Token);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
                { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach (var resource in new[] { "BrandPalette", "SharedStyles" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/CRS.Desktop.Wpf;component/Styles/{resource}.xaml") });
            var view = new VpnSettingsView(new BackgroundPipeClient(pipe));
            var host = new Border
            {
                Child = view, Padding = new Thickness(24), Background = (Brush)app.FindResource("BrandSurfaceBrush")
            };
            var window = new Window { Content = host, Width = 735, Height = 650, Title = "VPN 设置" };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await fixture.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var list = (ItemsControl)view.FindName("NodeList");
                    var address = (TextBox)view.FindName("SubscriptionInput");
                    var refresh = (Wpf.Ui.Controls.Button)view.FindName("RefreshButton");
                    var status = (TextBlock)view.FindName("Status");
                    var details = (ItemsControl)view.FindName("SubscriptionDetails");
                    var regions = (ListBox)view.FindName("Regions");
                    var count = (TextBlock)view.FindName("NodeCount");
                    if (address.Text != VpnFixture.Address || list.Items.Count != 1 ||
                        details.Items.Count != 1 || refresh.IsEnabled || !status.Text.Contains("测速"))
                        throw new InvalidOperationException("进入 VPN 设置后应先展示已有节点，并自动加载测速。");

                    fixture.CompleteRefresh.SetResult();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (!refresh.IsEnabled)
                        await Task.Delay(20, timeout.Token);

                    window.UpdateLayout();
                    var expected = new[]
                    {
                        ("82 ms", "BrandStatusSuccessBrush"), ("126 ms", "BrandStatusSuccessBrush"),
                        ("206 ms", "BrandStatusWarningBrush"), ("265 ms", "BrandStatusWarningBrush"),
                        ("354 ms", "BrandStatusErrorBrush"), ("未测得", "BrandStatusInfoBrush")
                    };
                    if (list.Items.Count != expected.Length || count.Text != "6 个" || details.Items.Count != 3 ||
                        status.Text.Length != 0)
                        throw new InvalidOperationException("节点刷新后应显示完整列表并结束加载状态。");
                    for (var i = 0; i < expected.Length; i++)
                    {
                        var item = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(i);
                        var latency = (TextBlock)list.ItemTemplate.FindName("LatencyText", item);
                        if (latency.Text != expected[i].Item1 || !ReferenceEquals(latency.Foreground, view.FindResource(expected[i].Item2)))
                            throw new InvalidOperationException("节点延迟排序或状态配色不正确。");
                    }

                    regions.SelectedItem = "日本";
                    if (list.Items.Count != 3)
                        throw new InvalidOperationException("日本标签应只展示日本节点。");
                    regions.SelectedItem = "全部";
                    window.UpdateLayout();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    var image = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(host);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(screenshot);
                    encoder.Save(output);
                }
                catch (Exception error) { failure = error; }
                finally { window.Close(); }
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        stop.Cancel();
        try { server.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (failure is not null) throw failure;
        Console.WriteLine("PASS VPN 自动加载测速、旧版套餐分离、节点计数、地区筛选和延迟显示。");
    }

    private sealed class VpnFixture : IBackgroundControl
    {
        internal const string Address = "https://example.org/synthetic-subscription";
        internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CompleteRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BackgroundResponse> HandleAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
        {
            VpnSettingsStatus result;
            if (request.Command == WorkerProtocol.GetVpnSettings && request.Payload == "ShowSubscription")
                result = new(true, true, ["香港 · 备用线路", "剩余流量：390.05 GB"]) { SubscriptionAddress = Address };
            else if (request.Command == WorkerProtocol.RefreshVpnNodes)
            {
                RefreshStarted.TrySetResult();
                await CompleteRefresh.Task.WaitAsync(token);
                result = new(true, true, ["美国 · 洛杉矶", "JPAWS日本01 | 电信移动联通推荐", "日本东京02-0.1倍",
                    "狮城 · 新加坡", "JPAWS日本06 | 电信移动联通推荐", "美国 · 纽约"])
                {
                    Delays = [354, 82, null, 206, 126, 265],
                    NodeTypes = ["Shadowsocks", "Vless", "Vless", "Trojan", "Vless", "Vless"],
                    SubscriptionDetails = [new("剩余流量", "390.05 GB"), new("套餐到期", "2029-05-25"), new("距离下次重置", "1 天")]
                };
            }
            else
                return new(WorkerProtocol.Version, request.RequestId, "InvalidRequest");
            return new(WorkerProtocol.Version, request.RequestId, "Ok", Payload: JsonSerializer.Serialize(result));
        }
    }
}
