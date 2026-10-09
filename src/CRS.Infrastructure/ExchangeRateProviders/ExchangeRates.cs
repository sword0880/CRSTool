using System.Globalization;
using System.Text.Json;
using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

public sealed class ExchangeRates : IExchangeRateProvider
{
    private readonly Dictionary<(int Year, string Currency), AppliedRate> rates = [];
    public string Fingerprint { get; }
    /// <summary>列出已登记的年度汇率及来源供前台复核。</summary>
    public List<AppliedRate> ForYear(int year) => rates.Where(r => r.Key.Year == year).Select(r => r.Value).OrderBy(r => r.Currency).ToList();
    /// <summary>加载年度汇率与来源元数据，并验证数值、币种、期间和临时标记。</summary>
    public ExchangeRates(string directory)
    {
        var valuesBytes = File.ReadAllBytes(Path.Combine(directory, "exchange_rate.json"));
        var sourcePath = Path.Combine(directory, "exchange_rate_sources.json");
        var sourceBytes = File.Exists(sourcePath) ? File.ReadAllBytes(sourcePath) : "{}"u8.ToArray();
        Fingerprint = XmlFields.HashJson(new { values = XmlFields.Hash(valuesBytes), sources = XmlFields.Hash(sourceBytes) });
        using var values = JsonDocument.Parse(valuesBytes); using var sources = JsonDocument.Parse(sourceBytes);
        if (values.RootElement.ValueKind != JsonValueKind.Object || sources.RootElement.ValueKind != JsonValueKind.Object) throw new CrsException("汇率及来源配置必须是 JSON 对象。");
        ValidateUnique(values.RootElement); ValidateUnique(sources.RootElement);
        foreach (var y in values.RootElement.EnumerateObject())
        {
            if (!int.TryParse(y.Name, out var year) || year is < 2000 or > 2100 || y.Value.ValueKind != JsonValueKind.Object) throw new CrsException("汇率年度格式无效。");
            var hasSource = sources.RootElement.TryGetProperty(y.Name, out var meta);
            DateOnly? from = null, to = null; bool provisional = false; string source = "来源未登记", url = "", method = "";
            if (hasSource)
            {
                source = Text(meta, "source"); url = Text(meta, "url"); method = Text(meta, "method");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new CrsException("汇率来源网址无效。");
                from = XmlFields.Date(Text(meta, "start_date")); to = XmlFields.Date(Text(meta, "end_date"));
                if (from > to || from.Value.Year != year || to.Value.Year != year) throw new CrsException("汇率来源期间无效。");
                var flag = meta.GetProperty("provisional");
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CrsException("汇率临时标记必须为布尔值。");
                provisional = flag.GetBoolean();
                if (to != new DateOnly(year, 12, 31) && !provisional) throw new CrsException("不足全年汇率必须标记为临时测算。");
            }
            foreach (var c in y.Value.EnumerateObject())
            {
                if (c.Name.Length != 3 || c.Name.Any(ch => ch is < 'A' or > 'Z')) throw new CrsException("币种须为三位大写字母。");
                var value = Numeric(c.Value);
                if (value <= 0 || (c.Name == "CNY" && value != 1)) throw new CrsException("汇率必须为有限正数，人民币汇率必须为 1。");
                var confirmed = c.Name == "CNY";
                if (hasSource && c.Name != "CNY")
                {
                    if (!meta.GetProperty("rates").TryGetProperty(c.Name, out var recorded) || Numeric(recorded) != value) throw new CrsException("汇率与来源文件记录不一致。");
                    confirmed = true;
                }
                if (!rates.TryAdd((year, c.Name), new(year, c.Name, value, c.Name == "CNY" ? "人民币本币" : source,
                    url, method, from, to, provisional && c.Name != "CNY", confirmed))) throw new CrsException("汇率年度或币种重复。");
            }
        }
    }
    /// <summary>查找指定年度币种的汇率，缺失时明确报错，不使用其他年度替代。</summary>
    public AppliedRate Get(int year, string currency) => rates.TryGetValue((year, currency), out var r) ? r : throw new CrsException($"缺少 {year} 年 {currency} 汇率。");
    /// <summary>读取来源文件的必填非空文本字段。</summary>
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()! : throw new CrsException($"汇率来源字段 {name} 无效。");
    /// <summary>解析数字或数字字符串，拒绝非有限值及超出 decimal 范围的配置。</summary>
    private static decimal Numeric(JsonElement e) => decimal.TryParse(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new CrsException("汇率必须是有限数字。");

    /// <summary>拒绝配置对象中的重复键，避免显示值与实际读取值不一致。</summary>
    private static void ValidateUnique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new CrsException("汇率配置存在重复字段。");
            ValidateUnique(property.Value);
        }
    }
}
