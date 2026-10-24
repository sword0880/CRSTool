using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;


/// <summary>易方达159915公开页面引用文件的隔离研究解析器；不执行脚本，不写正式事实库或扩大网络白名单。</summary>
public static class EfundsNavResearch
{
    public const string ParserVersion = "efunds-159915-nav-research-v1";
    public const string ProductUrl = "https://www.efunds.com.cn/fund/159915.shtml";
    public const string ArchiveUrl = "https://cdn.efunds.com.cn/market/2.0/his/159915_all.js";
    public const string RecentUrl = "https://cdn.efunds.com.cn/market/2.0/159915_1y.js";
    public const string DisplayUrl = "https://www.efunds.com.cn/js/fund_details/product-detail-overview.js?7905e4a6";
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly string[] SourceUrls = [ProductUrl, ArchiveUrl, RecentUrl, DisplayUrl];
    /// <summary>仅开放产品页直接引用且已经核对的四个地址，不允许任意基金代码或资源路径。</summary>
    public static IReadOnlyList<string> OfficialUrls => Array.AsReadOnly(SourceUrls);
    public static bool IsSource(Uri uri) => SourceUrls.Contains(uri.AbsoluteUri, StringComparer.Ordinal);
    private static readonly DateOnly Inception = new(2011, 9, 20);
    private sealed record Value(DateOnly Date, decimal UnitNav, decimal AccumulatedNav);

    public static NavResearchDataset Build(IReadOnlyList<CapturedNavDocument> documents, DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count != SourceUrls.Length || documents.Select(x => x.Url).Distinct(StringComparer.Ordinal).Count() != SourceUrls.Length
            || documents.Any(x => !SourceUrls.Contains(x.Url, StringComparer.Ordinal)))
            throw new InvalidDataException("研究样本必须恰好包含已核对的四个官方原件，拒绝替换代码、地址或额外来源。");
        // 复制输入后再校验，防止调用方在哈希验证与解析之间修改字节。
        var captured = documents.Select(x => x with { Content = x.Content.ToArray() }).ToDictionary(x => x.Url, StringComparer.Ordinal);
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in captured.Values)
        {
            if (document.Content.Length is < 1 or > MaxBytes || document.RetrievedAt == default || document.RetrievedAt > verifiedAt
                || !string.Equals(Convert.ToHexString(SHA256.HashData(document.Content)), document.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原件大小、取得时间或哈希无效，停止净值研究解析。");
            try { text.Add(document.Url, StrictUtf8.GetString(document.Content).TrimStart('\uFEFF')); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("净值原件不是严格UTF-8。", error); }
        }
        var page = text[ProductUrl];
        // 页面不同作用域会重复声明同一代码；所有声明都必须一致，不能只找一处正确文字。
        foreach (var variable in new[] { "fundCode", "maincode" })
        {
            var declarations = Regex.Matches(page, "var\\s+" + variable + "\\s*=\\s*(?<value>[^;\\r\\n]+)", RegexOptions.CultureInvariant, PatternTimeout);
            if (declarations.Count == 0 || declarations.Any(x => x.Groups["value"].Value.Trim() is not ("\"159915\"" or "'159915'")))
                throw new InvalidDataException("产品页基金代码缺失或不一致。");
        }
        foreach (var url in new[] { ArchiveUrl, RecentUrl, DisplayUrl })
            RequireSingle(page, "<script\\s+src=\"" + Regex.Escape(url) + "\"\\s*>\\s*</script>", "产品页未引用约定的净值/字段说明原件。");
        // 字段编号由官方显示脚本解释，不能只猜第三列或把累计净值当成单位净值。
        var names = RequireSingle(text[DisplayUrl], @"var\s+yNames\s*=\s*\[(?<names>[^\]]+)\]\s*;", "官方字段映射缺失或重复。").Groups["names"].Value;
        var expectedNames = new[] { "Date", "AccIncomeRatio", "NetValue", "AccNetValue", "Change", "IncomeRatio", "IncomeUnit", "HundredIncomeUnit", "TrackIndex" };
        var expected = string.Join(',', expectedNames.Select(x => "\"EnumSourceType" + x + "\""));
        if (Regex.Replace(names, @"\s", "", RegexOptions.CultureInvariant, PatternTimeout) != expected)
            throw new InvalidDataException("官方净值字段映射发生变化，拒绝沿用旧列定义。");

        var merged = new SortedDictionary<DateOnly, (Value Row, List<NavResearchSource> Sources)>();
        foreach (var (url, suffix) in new[] { (ArchiveUrl, "all"), (RecentUrl, "1y") })
        {
            var source = captured[url];
            var sourceDay = DateOnly.FromDateTime(source.RetrievedAt.ToOffset(TimeSpan.FromHours(8)).DateTime);
            foreach (var row in ParseScript(text[url], suffix, sourceDay))
            {
                var receipt = Receipt(source);
                if (merged.TryGetValue(row.Date, out var old))
                {
                    if (old.Row != row) throw new InvalidDataException("较老历史与最近一年净值冲突，整份研究样本拒绝，不能覆盖择优。");
                    old.Sources.Add(receipt);
                }
                else merged.Add(row.Date, (row, [receipt]));
            }
        }
        var (latest, officialCount) = ParsePage(page);
        if (merged.Count != officialCount) throw new InvalidDataException("合并历史条数与官方净值列表总数不符，可能截断或跨请求变更。");
        if (latest[0].Date != merged.Keys.Last()) throw new InvalidDataException("最近历史文件落后于产品页，不能默认为完整。");
        foreach (var row in latest)
            if (!merged.TryGetValue(row.Date, out var actual) || row != actual.Row)
                throw new InvalidDataException("历史净值与产品页同日单位/累计净值不符。");

        var points = merged.Values.Select(x => new NavResearchPoint(x.Row.Date, x.Row.UnitNav, x.Row.AccumulatedNav,
            x.Sources.Min(s => s.RetrievedAt), Array.AsReadOnly(x.Sources.Select(s => s.Sha256).Order(StringComparer.Ordinal).ToArray()))).ToArray();
        var sources = SourceUrls.Select(url => Receipt(captured[url])).ToArray();
        // 取得/验证时间也是研究输入；同样数值的后来快照不能复用较早知识身份。
        var canonical = ParserVersion + "|SZSE|159915|" + verifiedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            + "|" + string.Join('|', sources.Select(s => $"{s.Url}:{s.Sha256}:{s.RetrievedAt.ToUniversalTime():O}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new("SZSE", "159915", ParserVersion, verifiedAt, fingerprint, Array.AsReadOnly(sources), Array.AsReadOnly(points));
    }

    private static NavResearchSource Receipt(CapturedNavDocument source) => new(source.Url,
        source.Sha256.ToUpperInvariant(), source.RetrievedAt, source.Content.Length);

    private static IReadOnlyList<Value> ParseScript(string script, string suffix, DateOnly sourceDay)
    {
        var envelope = Regex.Match(script, "\\Amk_159915_" + suffix + "=\"(?<body>[0-9_.;\\-]+)\";\\s*\\z",
            RegexOptions.CultureInvariant, PatternTimeout);
        if (!envelope.Success) throw new InvalidDataException("净值文件不是约定的纯数据赋值，拒绝执行或容忍额外脚本。");
        var parts = envelope.Groups["body"].Value.Split(';');
        if (parts[0] != "0_1_2_3_4" || parts[^1] != "") throw new InvalidDataException("净值列编号或完整结束符改变。");
        var rows = new List<Value>();
        DateOnly? previous = null;
        foreach (var part in parts.Skip(1))
        {
            // 官方旧文件存在年度分隔空项；它们不是缺失日的零净值。
            if (part.Length == 0) continue;
            var columns = part.Split('_');
            if (columns.Length != 5 || !DateOnly.TryParseExact(columns[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < Inception || date > sourceDay || (previous is { } prior && date <= prior))
                throw new InvalidDataException("净值日期重复、乱序、早于成立或晚于原件取得日。");
            _ = Number(columns[1], false);
            _ = Number(columns[4], false);
            var unit = Number(columns[2], true);
            var accumulated = Number(columns[3], true);
            rows.Add(new(date, unit, accumulated)); previous = date;
        }
        if (rows.Count is < 1 or > 20000) throw new InvalidDataException("净值历史条数无效。");
        return rows;
    }

    private static (IReadOnlyList<Value> Rows, int Total) ParsePage(string page)
    {
        var head = RequireSingle(page, "<thead id=\"navTHead\">(?<body>.*?)</thead>", "官方净值表头缺失或重复。").Groups["body"].Value;
        var headers = Regex.Matches(head, @"<th>(?<value>[^<]*)</th>", RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout)
            .Select(m => WebUtility.HtmlDecode(m.Groups["value"].Value).Trim()).ToArray();
        if (!headers.SequenceEqual(new[] { "日期", "单位净值(元)", "日涨跌", "累计净值(元)", "资产净值(元)" }))
            throw new InvalidDataException("净值表列名或元单位变化，停止解析。");
        var body = RequireSingle(page, "<tbody id=\"navTBody\">(?<body>.*?)</tbody>", "官方净值表体缺失或重复。").Groups["body"].Value;
        var rows = new List<Value>(); int? total = null;
        const string rowPattern = "<tr data-total=\"(?<total>[0-9]+)\">(?<cells>.*?)</tr>";
        if (!string.IsNullOrWhiteSpace(Regex.Replace(body, rowPattern, "", RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout)))
            throw new InvalidDataException("净值列表存在未识别内容，不能跳过坏行。");
        foreach (Match row in Regex.Matches(body, rowPattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout))
        {
            if (!int.TryParse(row.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 20000
                || (total is { } old && old != count)) throw new InvalidDataException("官方净值总数无效或同页不一致。");
            total = count;
            var cells = Regex.Matches(row.Groups["cells"].Value, "<td(?: class=\"[a-z]+\")?>(?<value>[^<]*)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout)
                .Select(m => WebUtility.HtmlDecode(m.Groups["value"].Value).Trim()).ToArray();
            if (cells.Length != 5 || !DateOnly.TryParseExact(cells[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || (rows.Count > 0 && date >= rows[^1].Date)) throw new InvalidDataException("净值列表日期、列数或倒序约定变化。");
            rows.Add(new(date, Number(cells[1], true), Number(cells[3], true)));
        }
        if (rows.Count != 10 || total is null) throw new InvalidDataException("本版约定核对最近10条净值，缺失或变更时拒绝样本。");
        return (rows, total.Value);
    }

    private static Match RequireSingle(string text, string pattern, string reason)
    {
        var matches = Regex.Matches(text, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant, PatternTimeout);
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException(reason);
    }

    private static decimal Number(string text, bool positive)
    {
        if (text.Length > 32 || !Regex.IsMatch(text, @"\A-?[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant, PatternTimeout)
            || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || (positive && value <= 0)) throw new InvalidDataException("净值数值格式、范围或正值约定无效。");
        return value;
    }
}
