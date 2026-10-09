using System.Text.RegularExpressions;
using CRS.Domain;

namespace CRS.Infrastructure;

/// <summary>富途年度股息、利息与其他收入表解析，Excel/PDF 共用同一行校验。</summary>
internal static class FutuDividendExcelImporter
{
    /// <summary>读取唯一收入工作表，支持中文和原版英文表头。</summary>
    public static List<FutuRow> Read(string path)
    {
        using var table = new FutuTable(path);
        var rows = table.Read("股息", false) ?? table.Read("Income", false) ?? throw new CrsException("未找到富途股息收入工作表。");
        if (rows.Count == 0) throw new CrsException("富途收入表没有记录，不能视为全年零收入。");
        return rows;
    }
    /// <summary>统一校验年度、币种及金额；缺币种时从所有收入字段识别，支持纯利息行。</summary>
    public static AnnualIncome Parse(FutuRow row, string file, int selectedYear, string account, ImportData data)
    {
        var names = new[] { ("账户名称", "Account"), ("全年股息", "Dividends"), ("全年利息", "Interest"), ("全年其他收入", "Other income") };
        foreach (var (cn, en) in names) if (!row.Values.ContainsKey(cn) && !row.Values.ContainsKey(en)) throw new CrsException($"【{row.Sheet}】缺少字段：{cn}。");
        var accountName = Value("账户名称", "Account"); if (accountName == "") throw new CrsException("富途收入表账户名称不能为空。");
        var rawYear = row["年份"] == "" ? row["Year"] : row["年份"];
        var filenameYear = Regex.Match(Path.GetFileNameWithoutExtension(file), @"(?<!\d)(20\d{2})(?!\d)");
        var year = rawYear is "" or "-" or "—" or "None" ? filenameYear.Success ? FutuFields.Year(filenameYear.Value) : selectedYear : FutuFields.Year(rawYear);
        if (rawYear is "" or "-" or "—" or "None")
        {
            data.Warnings.Add($"收入表未注明年度，按 {year} 年处理，请确认报告期间。");
            data.Issues.Add(new("INCOME_YEAR_UNCONFIRMED", "收入表未列年度，已采用文件名或所选年度，需核对报告期间。", account, File: file));
        }
        var dividendText = Value("全年股息", "Dividends"); var interestText = Value("全年利息", "Interest"); var otherText = Value("全年其他收入", "Other income");
        var currencies = new HashSet<string>(); var explicitCurrency = row["币种"] == "" ? row["Currency"] : row["币种"];
        if (explicitCurrency != "") currencies.Add(FutuFields.Currency(explicitCurrency));
        foreach (var text in new[] { dividendText, interestText, otherText })
        {
            var prefix = Regex.Match(text.Trim(), @"^([A-Za-z]{3})(?=[\s\d+\-]|$)");
            if (prefix.Success) currencies.Add(FutuFields.Currency(prefix.Groups[1].Value));
        }
        if (currencies.Count != 1) throw new CrsException($"收入表第 {row.Row} 行币种缺失或字段币种相互冲突。");
        var currency = currencies.Single(); var dividend = FutuFields.Money(dividendText, "全年股息"); var interest = FutuFields.Money(interestText, "全年利息"); var other = FutuFields.Money(otherText, "全年其他收入");
        if (dividend < 0 || interest < 0) data.Issues.Add(new("NEGATIVE_INCOME", "年度存在净负收入，需核对冲正归属。", account, Currency: currency, File: file));
        if (other != 0) data.Issues.Add(new("OTHER_INCOME", "其他收入尚未分类计税，保留原值供复核。", account, Currency: currency, File: file));
        return new(account, accountName, year, currency, dividend, interest, other, file, row.Sheet, row.Row);

        /// <summary>读取明确的中英文别名，避免按列位置猜测金额含义。</summary>
        string Value(string cn, string en) => row.Values.ContainsKey(cn) ? row[cn] : row[en];
    }
}
