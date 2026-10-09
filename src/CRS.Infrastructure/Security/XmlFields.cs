using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CRS.Domain;

namespace CRS.Infrastructure;

internal static class XmlFields
{
    public const int MaxBytes = 25 * 1024 * 1024;
    /// <summary>读取必填属性；空值直接拒绝，防止缺失字段被当成零。</summary>
    public static string Required(XElement e, string field) => string.IsNullOrWhiteSpace(e.Attribute(field)?.Value)
        ? throw new CrsException($"IBKR 缺少字段 {field}，请补充导出。") : e.Attribute(field)!.Value.Trim();
    /// <summary>读取可选属性，仅在属性不存在时返回约定默认值。</summary>
    public static string Optional(XElement e, string field, string fallback = "") => e.Attribute(field)?.Value.Trim() ?? fallback;
    /// <summary>将必填属性解析为十进制金额或数量。</summary>
    public static decimal Number(XElement e, string field) => ParseNumber(Required(e, field), field);
    /// <summary>使用固定文化格式解析有限十进制数，拒绝 NaN、无穷大与越界数值。</summary>
    public static decimal ParseNumber(string value, string field)
    {
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new CrsException($"字段 {field} 必须为范围内的有限十进制数。");
        return number;
    }
    /// <summary>严格解析 Flex 的两种日期格式，不接受小数或模糊日期。</summary>
    public static DateOnly Date(string value)
    {
        if (!DateOnly.TryParseExact(value, ["yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new CrsException("日期格式须为 yyyyMMdd 或 yyyy-MM-dd。");
        return day;
    }
    /// <summary>解析日期或时间，并保留是否明确含时区；无时区时间不依赖运行机器时区。</summary>
    public static (DateTimeOffset Value, bool HasOffset) Time(string value)
    {
        if (value.Length is 8 or 10)
            return (new DateTimeOffset(Date(value).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), false);
        var offset = Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$");
        if (offset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) return (stamp, true);
        if (DateTime.TryParseExact(value, ["yyyyMMdd;HHmmss", "yyyyMMdd;HH:mm:ss", "yyyy-MM-dd;HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss", "yyyyMMdd HHmmss", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return (new DateTimeOffset(time, TimeSpan.Zero), false);
        throw new CrsException("成交时间须包含日期和时分秒。");
    }
    /// <summary>限量读取本地 XML，单文件最多 25 MB。</summary>
    public static byte[] ReadBytes(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxBytes) throw new CrsException("单份 XML 不能超过 25 MB。");
        using var memory = new MemoryStream(); file.CopyTo(memory);
        return memory.ToArray();
    }
    /// <summary>安全读取 XML 并统一元素命名空间；禁止 DTD 和外部实体。</summary>
    public static XDocument ReadXml(byte[] raw)
    {
        if (raw.Length > MaxBytes) throw new CrsException("XML 超过 25 MB。");
        try
        {
            // 禁用 DTD 和外部实体，避免本地或远程实体解析。
            using var stream = new MemoryStream(raw);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            var document = XDocument.Load(reader);
            foreach (var e in document.Descendants()) e.Name = e.Name.LocalName;
            return document;
        }
        catch (XmlException) { throw new CrsException("文件不是有效或受支持的 XML。"); }
    }
    /// <summary>计算文件内容的 SHA256，供来源追溯及变更检测使用。</summary>
    public static string Hash(byte[] raw) => Convert.ToHexStringLower(SHA256.HashData(raw));
    /// <summary>对确定的 JSON 表示计算摘要，用于语义去重及结转完整性校验。</summary>
    public static string HashJson(object value) => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
}
