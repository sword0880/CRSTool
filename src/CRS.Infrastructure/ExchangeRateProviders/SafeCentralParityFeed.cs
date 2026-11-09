using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CRS.Infrastructure;

internal sealed record CentralParityQuote(DateOnly Date, decimal Usd, decimal Hkd);

/// <summary>读取国家外汇管理局公开的人民币中间价查询结果。</summary>
internal static class SafeCentralParityFeed
{
    internal const string Url = "https://www.safe.gov.cn/AppStructured/hlw/RMBQuery.do";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    // 查询结果嵌套在页面布局表格中，匹配报价行时跳过外层行。
    private static readonly Regex Rows = Pattern(@"<tr\b[^>]*>((?:(?!<tr\b).)*?)</tr\s*>");
    private static readonly Regex Cells = Pattern(@"<t[dh]\b[^>]*>(.*?)</t[dh]\s*>");
    private static readonly Regex Tags = Pattern(@"<!--.*?-->|<[^>]*>");

    internal static async Task<CentralParityQuote[]> ReadAsync(HttpClient client, DateOnly from, DateOnly to, CancellationToken token)
    {
        var uri = new Uri(FormattableString.Invariant($"{Url}?startDate={from:yyyy-MM-dd}&endDate={to:yyyy-MM-dd}&queryYN=true"));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, token).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        try { return Parse(html, from, to); }
        catch (RegexMatchTimeoutException) { throw new CrsException("官方汇率表格解析超时。"); }
    }

    internal static CentralParityQuote[] Parse(string html, DateOnly from, DateOnly to)
    {
        var quotes = new Dictionary<DateOnly, CentralParityQuote>();
        (int Date, int Usd, int Hkd)? columns = null;
        foreach (System.Text.RegularExpressions.Match row in Rows.Matches(html))
        {
            var values = Cells.Matches(row.Groups[1].Value)
                .Select(c => WebUtility.HtmlDecode(Tags.Replace(c.Groups[1].Value, "")).Trim()).ToArray();
            if (columns is null)
            {
                var header = (Date: Array.IndexOf(values, "日期"), Usd: Array.IndexOf(values, "美元"), Hkd: Array.IndexOf(values, "港元"));
                if (header.Date >= 0 && header.Usd >= 0 && header.Hkd >= 0) columns = header;
                continue;
            }

            var (dateColumn, usdColumn, hkdColumn) = columns.Value;
            if (values.Length <= dateColumn || !DateOnly.TryParseExact(values[dateColumn], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (values.Length <= Math.Max(usdColumn, hkdColumn) || date < from || date > to)
                throw new CrsException("官方报价的日期或币种列与查询范围不符。");
            var quote = new CentralParityQuote(date, DirectRate(values[usdColumn]), DirectRate(values[hkdColumn]));
            if (!quotes.TryAdd(date, quote)) throw new CrsException("官方报价包含重复日期。");
        }
        return quotes.Values.OrderBy(q => q.Date).ToArray();
    }

    private static decimal DirectRate(string value)
    {
        // 外汇管理局的美元、港元按 100 外币兑人民币列示。
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount is <= 0 or > 100_000)
            throw new CrsException("官方美元或港元中间价无效。");
        return amount / 100m;
    }

    private static Regex Pattern(string expression) => new(expression,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
}
