using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class PostBodyViewer : WatchEditor
{
    private PostBodyViewer(Window? owner, PostBodyRequest post, PostBodyDocument? document,
        DateTimeOffset? fallbackTime) : base(owner, "雪球原帖正文", 720)
    {
        if (post.Revision > 0 && document is null) throw new ArgumentException("已关联的正文版本不存在。");
        Fields.Children.Add(new TextBlock
        {
            Text = document is null ? "暂无已获取的全文。" :
                $"全文 · {PostBody.SourceLabel(document.Info.Source)} · 版本 {document.Info.Revision}",
            TextWrapping = TextWrapping.Wrap
        });
        var published = (document?.Info.PublishedAt ?? fallbackTime)?.ToOffset(TimeSpan.FromHours(8));
        if (published is not null)
            Fields.Children.Add(new TextBlock { Text = $"发布于 {published:yyyy-MM-dd HH:mm}（北京时间）" });
        if (document is not null)
            Field("正文", new TextBox
            {
                Text = document.Text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                Height = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

        Finish(Action("打开雪球原帖", () =>
        {
            try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(post.Url)) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "无法打开雪球原帖。"); }
        }), Action("关闭", Close));
    }

    internal static async Task OpenAsync(Window? owner, WorkerWatchClient client, PostBodyRequest post,
        DateTimeOffset? fallbackTime, CancellationToken token)
    {
        var document = await client.ReadPostBodyAsync(post, token);
        new PostBodyViewer(owner, post, document, fallbackTime).ShowDialog();
    }
}
