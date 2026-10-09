using System.Globalization;
using System.Text.RegularExpressions;
using CRS.Domain;
using NPOI.SS.UserModel;

namespace CRS.Infrastructure;

/// <summary>NPOI 表格读取与富途字段转换；公式不执行，记录原始工作表行号。</summary>
internal sealed record FutuRow(string Sheet, int Row, Dictionary<string, string> Values)
{
    /// <summary>读取可选字段，缺失列返回空文本供调用方作明确校验。</summary>
    public string this[string field] => Values.GetValueOrDefault(field, "").Trim();
}

internal sealed class FutuTable : IDisposable
{
    private readonly IWorkbook workbook;
    /// <summary>限量打开工作簿并将底层格式异常转换为中文业务错误。</summary>
    public FutuTable(string path)
    {
        FutuFields.CheckFile(path);
        try { using var stream = File.OpenRead(path); workbook = WorkbookFactory.Create(stream); }
        catch (Exception e) when (e is not CrsException) { throw new CrsException("无法读取富途 Excel，请检查文件是否损坏或加密。"); }
    }
    /// <summary>按唯一关键词匹配工作表，拒绝歧义，必填表缺失时报错。</summary>
    public List<FutuRow>? Read(string keyword, bool required, params string[] columns)
    {
        var names = Enumerable.Range(0, workbook.NumberOfSheets).Select(workbook.GetSheetName).Where(n => n.Contains(keyword)).ToArray();
        if (names.Length > 1) throw new CrsException($"存在多个包含【{keyword}】的工作表，请明确文件范围。");
        if (names.Length == 0) { if (required) throw new CrsException($"未找到【{keyword}】工作表。"); return null; }
        var sheet = workbook.GetSheet(names[0]);
        if (sheet.LastRowNum > 100000) throw new CrsException("富途工作表不能超过 100000 行。");
        var header = sheet.GetRow(0) ?? throw new CrsException($"【{names[0]}】缺少表头。");
        if (header.LastCellNum > 1000) throw new CrsException("富途工作表列数过多。");
        var fields = Enumerable.Range(0, Math.Max(0, (int)header.LastCellNum)).Select(i => Text(header.GetCell(i))).ToArray();
        if (fields.Where(f => f != "").GroupBy(f => f).Any(g => g.Count() > 1)) throw new CrsException($"【{names[0]}】存在重复列名。");
        var missing = columns.Where(c => !fields.Contains(c)).ToArray();
        if (missing.Length > 0) throw new CrsException($"【{names[0]}】缺少字段：{string.Join("、", missing)}。");
        var result = new List<FutuRow>();
        for (var i = 1; i <= sheet.LastRowNum; i++)
        {
            var row = sheet.GetRow(i); if (row is null) continue;
            var values = fields.Select((f, c) => (f, value: Text(row.GetCell(c)))).Where(v => v.f != "").ToDictionary(v => v.f, v => v.value);
            if (values.Values.Any(v => v != "")) result.Add(new(names[0], i + 1, values));
        }
        return result;
    }
    /// <summary>读取真实单元格值；数字不用显示格式截断，日期保持时间精度。</summary>
    private static string Text(ICell? cell)
    {
        if (cell is null || cell.CellType == CellType.Blank) return "";
        if (cell.CellType is CellType.Formula or CellType.Error) throw new CrsException($"富途源表第 {cell.RowIndex + 1} 行含公式或错误单元格，请提供券商原始值。");
        if (cell.CellType == CellType.Numeric)
            return DateUtil.IsCellDateFormatted(cell) ? cell.DateCellValue?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? ""
                : cell.NumericCellValue.ToString("R", CultureInfo.InvariantCulture);
        return cell.ToString()?.Replace('\u3000', ' ').Trim() ?? "";
    }
    /// <summary>关闭 NPOI 工作簿，释放文件资源。</summary>
    public void Dispose() => workbook.Dispose();
}

internal static class FutuFields
{
    /// <summary>检查源文件存在且不超过 25 MB。</summary>
    public static void CheckFile(string path)
    {
        if (!File.Exists(path)) throw new CrsException("富途源文件不存在。");
        if (new FileInfo(path).Length is <= 0 or > 25 * 1024 * 1024) throw new CrsException("富途源文件必须非空且不能超过 25 MB。");
    }
    /// <summary>区分缺失金额和零，支持收入表的币种前缀与千位分隔符。</summary>
    public static decimal? OptionalMoney(string text, string field)
    {
        if (text.Trim() is "" or "-" or "—" or "None") return null;
        text = Regex.Replace(text.Trim(), "^[A-Za-z]{3}\\s*(?=[+\\-0-9])", "").Replace(",", "").Replace(" ", "");
        // Excel 数值型碎股可能以科学计数法显示，直接解析为 decimal，避免先转浮点再舍入。
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value))
            throw new CrsException($"字段【{field}】必须是有限且有效的数字。");
        return value;
    }
    /// <summary>将允许为空的金额转换为零，不接受 NaN 或无穷大。</summary>
    public static decimal Money(string text, string field) => OptionalMoney(text, field) ?? 0;
    /// <summary>严格检查四位整数年度，不截断小数。</summary>
    public static int Year(string text)
    {
        if (!Regex.IsMatch(text, "^(?:20[0-9]{2}|2100)$")) throw new CrsException("年份必须是 2000 至 2100 的四位整数，不能包含小数。");
        return int.Parse(text, CultureInfo.InvariantCulture);
    }
    /// <summary>检查富途当前支持的原币，不将未知币种猜测为美元。</summary>
    public static string Currency(string text)
    {
        var c = text.Trim().ToUpperInvariant();
        if (c is not ("USD" or "HKD" or "CNY")) throw new CrsException($"暂不支持或缺少币种：{c}。");
        return c;
    }
    /// <summary>解析无时区成交时间，固定占位偏移但保持 HasOffset=false。</summary>
    public static DateTimeOffset Time(string text)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd", "yyyyMMdd", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss" };
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new CrsException("富途日期或成交时间格式无法识别。");
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero);
    }
    /// <summary>转换券商日期，避免使用机器时区改变报告日期。</summary>
    public static DateOnly Date(string text) => DateOnly.FromDateTime(Time(text).DateTime);
    /// <summary>构造账户、市场、原始代码名称和币种隔离的富途证券标识。</summary>
    public static SecurityKey Key(string account, string symbol, string currency, string market)
        => new(account, symbol.Trim().ToUpperInvariant(), Currency(currency), "FUTU", market.Trim().ToUpperInvariant());
}
