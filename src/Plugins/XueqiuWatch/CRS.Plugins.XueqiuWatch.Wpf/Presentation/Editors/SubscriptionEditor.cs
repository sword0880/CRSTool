using System.Windows;
using System.Windows.Controls;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class SubscriptionEditor : WatchEditor
{
    internal string? SyncStatus { get; private set; }

    internal SubscriptionEditor(Window owner, WorkerWatchClient client, CancellationToken lifetime)
        : base(owner, "连接 Google Alerts", 540)
    {
        Fields.Children.Add(new TextBlock
        {
            Text = "首次使用时，在小财牛打开的 Edge 窗口登录 Google。若 CRS 已有订阅，请使用创建该订阅的账号。登录后关闭该 Edge 窗口，点击「立即同步」。以后修改观察名单时，小财牛会自动更新 RSS。",
            TextWrapping = TextWrapping.Wrap
        });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        Fields.Children.Add(status);
        Finish(AsyncAction("登录 Google", () => WithFeedbackAsync(() =>
        {
            GoogleAlertsSync.OpenSignIn();
            status.Text = "请在新打开的 Edge 窗口登录 Google。";
            return Task.CompletedTask;
        }, lifetime)), AsyncAction("立即同步", () => WithFeedbackAsync(async () =>
        {
            status.Text = "正在同步订阅…";
            SyncStatus = await new GoogleAlertsSync(client).SyncAsync(lifetime);
            status.Text = SyncStatus;
        }, lifetime)), Action("关闭", Close));
    }
}
