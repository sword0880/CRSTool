using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Win32;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class OpinionEditor : WatchEditor
{
    private readonly WorkerWatchClient client;
    private readonly CancellationToken lifetime;
    private readonly Security[] directory;
    private readonly Opinion? original;
    private readonly PostBodyDocument? postBody;
    private readonly PostLead? lead;
    private readonly Guid id;
    private readonly ComboBox author = new() { DisplayMemberPath = nameof(Author.Name) };
    private readonly TextBox time = new() { MaxLength = 16 };
    private readonly ComboBox market = new();
    private readonly TextBox code = new() { MaxLength = 12 };
    private readonly TextBox name = new() { MaxLength = 50 };
    private readonly TextBox industry = new() { MaxLength = 30 };
    private readonly ComboBox stance = new();
    private readonly ComboBox attribution = new();
    private readonly TextBox content = Multiline(72, 700);
    private readonly TextBox evidence = Multiline(82, 32768);
    private readonly TextBox source = new() { MaxLength = 250 };
    private readonly TextBox question = new() { MaxLength = 180 };
    private readonly TextBox finding = new() { MaxLength = 180 };
    private readonly ComboBox candidates = new();
    private readonly TextBlock inputStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    internal Opinion? SavedOpinion { get; private set; }

    public OpinionEditor(Window? owner, Author[] authors, Security[] directory, Opinion? original,
        Security? selectedStock, WorkerWatchClient client, CancellationToken lifetime, PostLead? lead = null,
        PostBodyDocument? postBody = null)
        : base(owner, original is null ? "录入雪球观点" : "编辑雪球观点", 690)
    {
        this.original = original;
        this.lead = lead;
        this.postBody = postBody;
        this.directory = directory;
        this.client = client;
        this.lifetime = lifetime;
        id = original?.Id ?? Guid.NewGuid();
        var selectedAccount = original?.Account ?? lead?.Account;
        var usableAuthors = authors.Where(x => !x.Archived || x.Account == selectedAccount).ToArray();
        author.ItemsSource = usableAuthors;
        author.SelectedItem = usableAuthors.FirstOrDefault(x => x.Account == selectedAccount);
        if (selectedAccount is null && usableAuthors.Length > 0) author.SelectedIndex = 0;
        // 首次发现时间不能作为原帖发布时间。
        var knownTime = original?.PublishedAt ?? postBody?.Info.PublishedAt ?? lead?.PublishedAt ??
            (lead is null ? WatchProtocol.BeijingNow : null);
        time.Text = knownTime?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        market.ItemsSource = WatchProtocol.Markets;
        SetStock(original?.Security ?? (original is null ? selectedStock : null));
        stance.ItemsSource = new[] { "待确认", "看多", "看空", "中性" };
        stance.SelectedIndex = (int)(original?.Stance ?? Stance.Pending);
        attribution.ItemsSource = new[] { "作者本人发言", "顺带提及", "转发或转述" };
        attribution.SelectedIndex = lead is null ? (int)(original?.Attribution ?? Attribution.Author) : -1;
        content.Text = original?.Content ?? LeadText(lead is null ? null : $"{lead.Title}\n{lead.Content ?? lead.Summary}", 700);
        evidence.Text = original?.Evidence ?? LeadText(lead?.Summary ?? lead?.Content, 700);
        source.Text = original?.Url ?? lead?.Url ?? "";
        question.Text = original?.Question ?? "";
        finding.Text = original?.Finding ?? "";
        if (lead is not null) inputStatus.Text = "已带入线索的作者和链接。请核对原帖，填写发言时间、归属及观点内容。";

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new());
        columns.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new(0, 0, 12, 0) };
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Fields.Children.Add(columns);

        Add(left, "作者", author);
        Add(right, "发言时间（北京时间）", time);
        Add(left, "市场", market);
        Add(right, "证券代码", code);
        Add(left, "股票名称", name);
        Add(right, "行业（选填）", industry);
        Add(left, "多空方向", stance);
        Add(right, "发言归属", attribution);
        TextBox? bodyText = null;
        if (postBody is not null)
        {
            bodyText = Multiline(150, 0);
            bodyText.IsReadOnly = true;
            bodyText.Text = postBody.Text;
            Field("原帖全文（手工补录）", bodyText);
        }
        Field("观点、理由与适用条件", content);
        Field("证据摘录或自有研究笔记（保存上限 700 字符）", evidence);
        var import = new WrapPanel { Margin = new(0, 5, 0, 1) };
        import.Children.Add(AsyncAction("导入 UTF-8 文本", () => WithFeedbackAsync(ImportAsync, lifetime)));
        import.Children.Add(Action("识别股票候选", Detect));
        if (bodyText is not null)
            import.Children.Add(Action("使用原文选中段落", () =>
            {
                var selected = bodyText.SelectedText.Trim();
                if (selected.Length is < 1 or > 700)
                {
                    inputStatus.Text = "请在全文中选取 1–700 个字符作为证据。";
                    return;
                }
                evidence.Text = selected;
            }));
        import.Children.Add(Action("保留所选摘录", () =>
        {
            if (!string.IsNullOrWhiteSpace(evidence.SelectedText)) evidence.Text = evidence.SelectedText;
        }));
        Fields.Children.Add(import);
        inputStatus.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        Fields.Children.Add(inputStatus);
        Field("候选股票（选择后核对市场和代码）", candidates);
        candidates.DisplayMemberPath = nameof(Candidate.Label);
        candidates.SelectionChanged += (_, _) =>
        {
            if (candidates.SelectedItem is Candidate row) SetStock(row.Security);
        };
        Field("雪球原帖链接（选填）", source);
        Field("待核实问题（选填）", question);
        Field("核实结果（选填）", finding);
        Finish(Action("取消", Close), AsyncAction("保存观点", SaveAsync));
        if (lead is not null || postBody is not null)
        {
            Detect();
            inputStatus.Text += postBody is null
                ? " 已带入 RSS 片段，请核对原帖。"
                : " 已加载手工补录的全文，请核对每只股票对应的段落和作者立场。";
        }
    }

    private static void Add(Panel parent, string description, Control control)
    {
        parent.Children.Add(new Label { Content = description, Target = control, Padding = new(0), Margin = new(0, 7, 0, 3) });
        System.Windows.Automation.AutomationProperties.SetName(control, description);
        parent.Children.Add(control);
    }

    private static TextBox Multiline(double height, int limit) => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, MaxLength = limit,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private void SetStock(Security? security)
    {
        market.SelectedItem = security?.Market;
        code.Text = security?.Code ?? "";
        name.Text = security?.Name ?? "";
        industry.Text = security?.Industry ?? "";
    }

    private static string LeadText(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        return text.Length <= maximum ? text : text[..maximum];
    }

    private void Detect()
    {
        var text = postBody?.Text ?? evidence.Text + "\n" + content.Text;
        var matches = WatchInput.Candidates(text, directory);
        candidates.ItemsSource = matches.Select(x => new Candidate(x, $"{x.Name} · {x.Market} {x.Code}")).ToArray();
        inputStatus.Text = matches.Count > 0 ? $"识别到 {matches.Count} 个候选，请核对后选择。" :
            "没有明确候选。可手工填写市场、代码和名称，或先保存为待确认。";
        var phrases = WatchInput.DirectionPhrases(text);
        if (phrases.Length != 0) inputStatus.Text += $" 发现措辞「{string.Join("、", phrases)}」，请结合上下文确认方向。";
    }

    private async Task ImportAsync()
    {
        var picker = new OpenFileDialog { Filter = "UTF-8 文本 (*.txt)|*.txt", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 32768) throw new ArgumentException("请选择 1 字节至 32 KiB 的 UTF-8 文本。");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, lifetime);
        try { evidence.Text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new ArgumentException("文本不是有效的 UTF-8 文件。"); }
        Detect();
        if (evidence.Text.Length > 700)
            inputStatus.Text += " 保存前，请选取需要留存的证据并点击「保留所选摘录」。";
    }

    private Task SaveAsync() => WithFeedbackAsync(async () =>
    {
        if (author.SelectedItem is not Author selectedAuthor) throw new ArgumentException("请选择作者。");
        if (!DateTime.TryParseExact(time.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)) throw new ArgumentException("发言时间格式为 yyyy-MM-dd HH:mm。");
        if (attribution.SelectedIndex < 0) throw new ArgumentException("请核对原帖并选择发言归属。");
        Security? security = null;
        if (market.SelectedItem is string || code.Text.Length != 0 || name.Text.Length != 0)
            security = WatchInput.CheckSecurity(new(market.SelectedItem as string ?? "", code.Text, name.Text, industry.Text));
        var url = WatchInput.SourceLink(source.Text);
        var bodyRevision = selectedAuthor.Account == (original?.Account ?? lead?.Account) &&
            url == (original?.Url ?? lead?.Url)
            ? postBody?.Info.Revision ?? original?.PostBodyRevision ?? 0 : 0;
        var draft = new Opinion(id, selectedAuthor.Account, security, new DateTimeOffset(parsed, TimeSpan.FromHours(8)),
            (Stance)stance.SelectedIndex, (Attribution)attribution.SelectedIndex, WatchInput.Text(content.Text, 700, true),
            WatchInput.Text(evidence.Text, 700), url, question.Text, finding.Text, original?.Revision ?? 0,
            PostBodyRevision: bodyRevision);
        WatchInput.CheckOpinion(draft);
        await client.SaveAsync(new("opinion-save", Opinion: draft), lifetime);
        SavedOpinion = draft;
        CloseAfterSave();
    }, lifetime);

    private sealed record Candidate(Security Security, string Label);
}
