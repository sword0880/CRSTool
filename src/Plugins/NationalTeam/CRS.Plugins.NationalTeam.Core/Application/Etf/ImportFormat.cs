using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
public sealed record ImportRow(EtfKey Key, DateOnly Date, decimal Original, string Unit);
// 这是人工核对后的标准化研究格式，不冒充已验证的交易所原生文件适配器。
public static class ImportFormat
{
    public static List<ImportRow> Shares(string text)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        if (lines.Length < 2 || lines[0] != "market,ticker,trade_date,total_shares,unit") throw new InvalidDataException("需要标准化CSV表头 market,ticker,trade_date,total_shares,unit；数值不得含千位分隔符。");
        var rows = new List<ImportRow>(); var keys = new HashSet<string>();
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length != 5 || cells[0] is not ("SSE" or "SZSE") || cells[1].Length != 6 || !cells[1].All(char.IsAsciiDigit)
                || !DateOnly.TryParseExact(cells[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !decimal.TryParse(cells[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) throw new InvalidDataException("CSV字段、日期或数值格式不符合规范。");
            _ = EtfMetrics.Normalize(value, cells[4]);
            if (!keys.Add(string.Join(',', cells.Take(3)))) throw new InvalidDataException("同一文件存在重复证券日期。");
            rows.Add(new(new(cells[0], cells[1]), date, value, cells[4]));
        }
        return rows;
    }
    public static List<(string Market, DateOnly Date)> Calendar(string text)
    {
        var lines = text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
        if (lines.Length < 2 || lines[0] != "market,trade_date") throw new InvalidDataException("交易日历表头必须为 market,trade_date。");
        return lines.Skip(1).Select(line =>
        {
            var cells = line.Split(',');
            if (cells.Length != 2 || cells[0] is not ("SSE" or "SZSE") || !DateOnly.TryParseExact(cells[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidDataException("交易日历格式无效。");
            return (cells[0], date);
        }).Distinct().ToList();
    }
}
