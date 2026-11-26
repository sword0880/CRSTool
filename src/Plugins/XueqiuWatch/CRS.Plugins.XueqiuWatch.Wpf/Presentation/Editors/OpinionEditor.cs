using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CRS.Plugins.XueqiuWatch.Core;

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
    private readonly TextBox evidence = Multiline(82, 700);
    private readonly TextBox source = new() { MaxLength = 250 };
    private readonly TextBox question = new() { MaxLength = 180 };
    private readonly TextBox finding = new() { MaxLength = 180 };
    private readonly ComboBox candidates = new();
    private readonly TextBlock inputStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private int taggedStockCount;
    internal Opinion? SavedOpinion { get; private set; }

    public OpinionEditor(Window? owner, Author[] authors, Security[] directory, Opinion? original,
        WorkerWatchClient client, CancellationToken lifetime, PostLead? lead = null,
        PostBodyDocument? postBody = null, Security? preferredStock = null)
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
        var knownTime = original?.PublishedAt ?? postBody?.Info.PublishedAt ??
            WatchInput.PostTime(lead?.PublishedAt, WatchProtocol.BeijingNow) ??
            (lead is null ? WatchProtocol.BeijingNow : null);
        time.Text = knownTime?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
        market.ItemsSource = WatchProtocol.Markets;
        SetStock(original?.Security);
        stance.ItemsSource = new[] { "待确认", "看多", "看空", "中性" };
        stance.SelectedIndex = (int)(original?.Stance ?? Stance.Pending);
        attribution.ItemsSource = new[] { "作者本人发言", "顺带提及", "转发或转述" };
        attribution.SelectedIndex = lead is null ? (int)(original?.Attribution ?? Attribution.Author) : -1;
        var leadExcerpt = lead is null ? "" : string.IsNullOrWhiteSpace(lead.Content) ? lead.Summary : lead.Content;
        content.Text = original?.Content ?? LeadText($"{lead?.Title}\n{leadExcerpt}", 700);
        evidence.Text = original?.Evidence ?? LeadText(leadExcerpt, 700);
        source.Text = original?.Url ?? lead?.Url ?? "";
        question.Text = original?.Question ?? "";
        finding.Text = original?.Finding ?? "";

        Field("原帖提及的股票", candidates);
        candidates.DisplayMemberPath = nameof(Candidate.Label);
        candidates.SelectionChanged += (_, _) =>
        {
            if (candidates.SelectedItem is Candidate row) SetStock(row.Security);
        };
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
            Field($"原帖全文（{PostBody.SourceLabel(postBody.Info.Source)}）", bodyText);
        }
        Field("观点、理由与适用条件", content);
        Field("证据摘录（最多 700 字符）", evidence);
        var actions = new WrapPanel { Margin = new(0, 5, 0, 1) };
        actions.Children.Add(Action("重新识别股票", () => Detect()));
        if (bodyText is not null)
            actions.Children.Add(Action("使用原文选中段落", () =>
            {
                var selected = bodyText.SelectedText.Trim();
                if (selected.Length is < 1 or > 700)
                {
                    inputStatus.Text = "请在全文中选取 1–700 个字符作为证据。";
                    return;
                }
                evidence.Text = selected;
            }));
        Fields.Children.Add(actions);
        inputStatus.SetResourceReference(TextBlock.ForegroundProperty, "BrandTextSecondaryBrush");
        Fields.Children.Add(inputStatus);
        Field("雪球原帖链接（选填）", source);
        Field("待核实问题（选填）", question);
        Field("核实结果（选填）", finding);
        Finish(Action("取消", Close), AsyncAction("保存观点", SaveAsync));
        Detect(preferredStock, fillFromTag: original is null);
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

    private void Detect(Security? preferredStock = null, bool fillFromTag = false)
    {
        var previous = candidates.SelectedItem as Candidate;
        var text = postBody is null
            ? $"{lead?.Title}\n{lead?.Summary}\n{lead?.Content}\n{evidence.Text}\n{content.Text}"
            : $"{postBody.Text}\n{lead?.Title}";
        var tags = WatchInput.StockTags(text).Select(tag => tag.Security.Key).Distinct().ToArray();
        taggedStockCount = tags.Length;
        var matches = WatchInput.Candidates(text, directory).ToList();
        if (preferredStock is not null && matches.All(stock => stock.Key != preferredStock.Key))
            matches.Insert(0, preferredStock);
        var options = matches.Select(x => new Candidate(x, $"{x.Name} · {x.Market} {x.Code}")).ToArray();
        candidates.ItemsSource = options;
        var selectedKey = preferredStock?.Key ?? previous?.Security.Key;
        if (selectedKey is null && fillFromTag && tags.Length == 1) selectedKey = tags[0];
        if (selectedKey is not null) candidates.SelectedItem = options.FirstOrDefault(row => row.Security.Key == selectedKey);
        inputStatus.Text = tags.Length switch
        {
            > 1 => $"原帖提及 {tags.Length} 只股票，选择一只即可带入代码和名称。请分别核对各股票的发言。",
            1 => "已识别原帖股票标签。请结合原文核对发言归属和方向。",
            _ when options.Length > 0 => "可选择识别出的股票候选。",
            _ => "没有明确候选。可手工填写市场、代码和名称，或先保存为待确认。"
        };
        var phrases = WatchInput.DirectionPhrases(text);
        if (phrases.Length != 0) inputStatus.Text += $" 发现措辞「{string.Join("、", phrases)}」，请结合上下文确认方向。";
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
        if (original is null && security is null && taggedStockCount > 0)
            throw new ArgumentException("请从原帖提及的股票中选择要核对的一只。");
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
