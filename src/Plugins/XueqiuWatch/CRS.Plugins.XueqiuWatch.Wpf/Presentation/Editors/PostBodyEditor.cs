using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Win32;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class PostBodyEditor : WatchEditor
{
    private const string DatePattern = "yyyy-MM-dd HH:mm";
    private readonly TextBox body = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 310,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBox posted = new() { MaxLength = 16 };
    private readonly CheckBox complete = new()
    {
        Content = "我已核对原帖和发布时间，并可保存这份完整正文。",
        Margin = new(0, 8, 0, 3)
    };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? activeUpload;

    private PostBodyEditor(Window? owner, WorkerWatchClient client, PostBodyRequest post,
        PostBodyDocument? document, DateTimeOffset? fallbackTime, CancellationToken token)
        : base(owner, "雪球原帖正文", 720)
    {
        var readOnly = post.Revision > 0;
        if (readOnly && document is null) throw new ArgumentException("已关联的正文版本不存在。");
        posted.IsReadOnly = body.IsReadOnly = readOnly;
        posted.Text = (document?.Info.PublishedAt ?? fallbackTime)?.ToOffset(TimeSpan.FromHours(8))
            .ToString(DatePattern, CultureInfo.InvariantCulture) ?? "";
        body.Text = document?.Text ?? "";
        body.TextChanged += (_, _) => complete.IsChecked = false;
        posted.TextChanged += (_, _) => complete.IsChecked = false;
        Fields.Children.Add(new TextBlock
        {
            Text = document is null ? "暂无全文。可粘贴原帖文字或导入 UTF-8 文本，最大 256 KiB。" :
                $"全文 · {PostBody.SourceLabel(document.Info.Source)} · 版本 {document.Info.Revision}",
            TextWrapping = TextWrapping.Wrap
        });
        Field("原帖发布时间（北京时间）", posted);
        Field("正文", body);
        if (!readOnly) Fields.Children.Add(complete);
        Fields.Children.Add(notice);
        Closing += (_, _) => activeUpload?.Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || activeUpload is null) return;
            activeUpload.Cancel();
            e.Handled = true;
        };
        var open = Action("打开雪球原帖", () =>
        {
            try { Process.Start(new ProcessStartInfo(WatchInput.SourceLink(post.Url)) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show(this, "无法打开雪球原帖。"); }
        });
        if (readOnly) Finish(open, Action("关闭", Close));
        else Finish(open,
            AsyncAction("导入 UTF-8 文本", () => WithFeedbackAsync(() => ImportAsync(token), token)),
            AsyncAction("保存正文", () => WithFeedbackAsync(async () =>
            {
                if (complete.IsChecked != true) throw new ArgumentException("请先确认正文完整并核实原帖时间。");
                if (!DateTime.TryParseExact(posted.Text.Trim(), DatePattern, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                    throw new ArgumentException("发言时间格式为 yyyy-MM-dd HH:mm。");
                using var upload = CancellationTokenSource.CreateLinkedTokenSource(token);
                activeUpload = upload;
                notice.Text = "正在保存正文。按 Esc 或关闭窗口可请求取消。";
                try
                {
                    await client.SavePostBodyAsync(post with { Revision = document?.Info.Revision ?? 0 }, body.Text,
                        new DateTimeOffset(time, TimeSpan.FromHours(8)), upload.Token);
                }
                catch (OperationCanceledException) when (upload.IsCancellationRequested)
                {
                    return;
                }
                finally
                {
                    activeUpload = null;
                    notice.Text = upload.IsCancellationRequested ? "已取消。可重新打开原帖确认最近保存的版本。" : "";
                }
                CloseAfterSave();
            }, token)), Action("关闭", Close));
    }

    internal static async Task<bool> OpenAsync(Window? owner, WorkerWatchClient client, PostBodyRequest post,
        DateTimeOffset? fallbackTime, CancellationToken token)
    {
        var document = await client.ReadPostBodyAsync(post, token);
        return new PostBodyEditor(owner, client, post, document, fallbackTime, token).ShowDialog() == true;
    }

    private async Task ImportAsync(CancellationToken token)
    {
        var picker = new OpenFileDialog { Filter = "UTF-8 文本 (*.txt)|*.txt", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > PostBody.MaximumBytes)
            throw new ArgumentException("请选择 1 字节至 256 KiB 的 UTF-8 文本。");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, token);
        if (file.ReadByte() != -1) throw new ArgumentException("文件在导入时已改变，请重新选择。");
        body.Text = PostBody.Decode(bytes).TrimStart('\uFEFF');
        complete.IsChecked = false;
    }
}
