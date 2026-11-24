using System.Text.RegularExpressions;

namespace CRS.Plugins.XueqiuWatch.Core;

public static partial class WatchInput
{
    public static string AccountId(string? input)
    {
        var value = (input ?? "").Trim();
        if (IsAccount(value)) return value;
        var uri = XueqiuUri(value);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && IsAccount(parts[0])) return parts[0];
        if (parts.Length == 2 && parts[0] == "u" && IsAccount(parts[1])) return parts[1];
        if (parts.Length == 2 && parts[1] == "column" && IsAccount(parts[0])) return parts[0];
        throw new ArgumentException("请输入雪球数字账号 ID 或含数字 ID 的主页链接。");
    }

    public static string SourceLink(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var uri = XueqiuUri(input.Trim());
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IsAccount(parts[0]) || !IsAccount(parts[1]))
            throw new ArgumentException("请输入包含作者 ID 和帖子 ID 的雪球原帖链接。");
        return $"https://xueqiu.com/{parts[0]}/{parts[1]}";
    }

    private static Uri XueqiuUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("xueqiu.com" or "www.xueqiu.com"))
            throw new ArgumentException("请输入 https://xueqiu.com 的雪球链接。");
        return uri;
    }


    /// <summary>把 Google Alerts 的跳转链接还原为经过作者归属校验的雪球原帖链接。</summary>
    public static string AlertSourceLink(string? input, string account)
    {
        var owner = AccountId(account);
        if (!Uri.TryCreate((input ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0)
            throw new ArgumentException("提醒结果不是有效的 HTTPS 文章链接。");

        var source = uri;
        if (IsGoogleAlertsHost(uri.Host))
        {
            if (!uri.AbsolutePath.Equals("/url", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不支持该 Google 跳转地址。");

            var target = QueryValue(uri, "url") ?? QueryValue(uri, "q");
            if (!Uri.TryCreate(target, UriKind.Absolute, out source))
                throw new ArgumentException("Google 提醒结果缺少有效的原文地址。");
        }

        var normalized = SourceLink(source.AbsoluteUri);
        if (!normalized.StartsWith($"https://xueqiu.com/{owner}/", StringComparison.Ordinal))
            throw new ArgumentException("文章链接与当前观察作者账号不一致。");
        return normalized;
    }

    /// <summary>生成按雪球用户主页路径监控的 Google Alerts 搜索条件。</summary>
    public static string GoogleAlertQuery(string account) => $"site:xueqiu.com/{AccountId(account)}";

    public static bool IsGoogleAlertsFeed(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps && IsGoogleAlertsHost(address.Host) &&
        address.AbsolutePath.StartsWith("/alerts/feeds/", StringComparison.Ordinal);

    private static bool IsGoogleAlertsHost(string host) =>
        host is "google.com" or "www.google.com" or "google.com.hk" or "www.google.com.hk";

    public static string NormalizeRssProxy(string? input)
    {
        var value = Text(input, 200);
        if (value.Length == 0) return "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var proxy) || proxy.Scheme != Uri.UriSchemeHttp ||
            !proxy.IsLoopback || proxy.Port < 1 || proxy.UserInfo.Length != 0 ||
            proxy.PathAndQuery != "/" || proxy.Fragment.Length != 0)
            throw new ArgumentException("请输入 Mihomo 本机 HTTP / Mixed 地址，例如 http://127.0.0.1:7890。");
        return proxy.AbsoluteUri;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(part[..separator].Replace('+', ' '));
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static bool IsAccount(string value) => value.Length is >= 1 and <= 20 && value.All(char.IsAsciiDigit);

    public static Security CheckSecurity(Security security)
    {
        var market = Text(security.Market, 10, true);
        var code = Text(security.Code, 12, true).ToUpperInvariant();
        switch (market)
        {
            case "港股" when code.Length <= 5 && code.All(char.IsAsciiDigit):
                code = code.PadLeft(5, '0');
                break;
            case "沪市" or "深市" or "北交所" when code.Length == 6 && code.All(char.IsAsciiDigit):
                break;
            case "美股" when UsCode().IsMatch(code):
                break;
            default:
                throw new ArgumentException("请检查市场和证券代码。");
        }
        return new(market, code, Text(security.Name, 50, true), Text(security.Industry, 30));
    }

    public static string Text(string? input, int maximum, bool required = false)
    {
        var value = (input ?? "").Trim();
        if (value.Length > maximum) throw new ArgumentException($"该字段限 {maximum} 个字符。");
        if (required && value.Length == 0) throw new ArgumentException("请填写必填字段。");
        return value;
    }

    public static void CheckOpinion(Opinion value)
    {
        if (value.Id == Guid.Empty || !Enum.IsDefined(value.Stance) || !Enum.IsDefined(value.Attribution))
            throw new ArgumentException("观点记录无效。");
        if (value.PublishedAt.Year < 2005 || value.PublishedAt > WatchProtocol.BeijingNow.AddMinutes(5))
            throw new ArgumentException("请填写有效的发言时间。");
        if (value.Security is null && value.Stance != Stance.Pending)
            throw new ArgumentException("请先确认股票，或保存为待确认。");
        if (value.Attribution != Attribution.Author && value.Stance is Stance.Bullish or Stance.Bearish)
            throw new ArgumentException("转述或顺带提及请单独记录。");
        Text(value.Content, 700, true);
        Text(value.Evidence, 700);
        Text(value.Question, 180);
        Text(value.Finding, 180);
    }

    /// <summary>只提取显式代码和已有目录内的唯一名称，候选仍须人工确认。</summary>
    public static IReadOnlyList<Security> Candidates(string text, IReadOnlyList<Security> directory)
    {
        var found = new Dictionary<string, Security>(StringComparer.Ordinal);
        foreach (Match match in TaggedCode().Matches(text))
        {
            var market = match.Groups["market"].Value.ToUpperInvariant() switch
            {
                "SH" => "沪市", "SZ" => "深市", "BJ" => "北交所", "HK" => "港股",
                "" when match.Groups["code"].Value.All(char.IsAsciiDigit) => "港股",
                _ => "美股"
            };
            try
            {
                var code = match.Groups["code"].Value;
                var name = match.Groups["name"].Value.Trim();
                var candidate = CheckSecurity(new(market, code, name.Length == 0 ? code : name, ""));
                found.TryAdd(candidate.Key, directory.FirstOrDefault(x => x.Key == candidate.Key) ?? candidate);
            }
            catch (ArgumentException) { /* 不把无法规范化的代码自动归属到股票。 */ }
        }
        foreach (var group in directory.GroupBy(x => x.Name, StringComparer.Ordinal))
        {
            if (group.Key.Length < 2 || group.Count() != 1 || !text.Contains(group.Key, StringComparison.Ordinal)) continue;
            var candidate = group.First();
            found.TryAdd(candidate.Key, candidate);
        }
        return found.Values.ToArray();
    }

    public static string[] DirectionPhrases(string text) =>
        OpinionTerms().Matches(text).Select(x => x.Value).Distinct(StringComparer.Ordinal).Take(6).ToArray();

    [GeneratedRegex("并非看多|并非看空|不是看多|不是看空|不再看多|不再看空|不看好|看多|看空|看好|观望|等季报确认", RegexOptions.CultureInvariant)]
    private static partial Regex OpinionTerms();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]{0,6}(?:[.-][A-Z0-9]{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex UsCode();

    [GeneratedRegex(@"(?:\$(?<name>[^$\r\n()]{0,50})\((?:(?<market>SH|SZ|BJ|HK|US)(?<code>[A-Z0-9.-]+)|(?<code>[0-9]{5}|[A-Z][A-Z0-9.-]{0,11}))\)\$|(?<![A-Z0-9])(?<market>SH|SZ|BJ|HK)[-:]?(?<code>[0-9]{4,6})(?![0-9])|(?<![A-Z0-9])(?<market>US)[:-](?<code>[A-Z][A-Z0-9.-]{0,11})(?![A-Z0-9]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaggedCode();
}
