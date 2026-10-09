using System.Text.RegularExpressions;
using CRS.Domain;

namespace CRS.Infrastructure.Pdf;

/// <summary>解析可提取文字的富途 CRS 收入表；优先官方英文页，避免三种语言重复计税。</summary>
public static class FutuDividendPdfImporter
{
    /// <summary>识别英文账户收入表并保持页码行号，不支持扫描件或无法可靠定位的布局。</summary>
    internal static List<FutuRow> Read(string path, CancellationToken cancellation)
    {
        FutuFields.CheckFile(path);
        try
        {
            var document = PdfReader.Read(path, cancellation);
            var result = new List<FutuRow>();
            for (var pageNumber = 1; pageNumber <= document.Count; pageNumber++)
            {
                cancellation.ThrowIfCancellationRequested(); var page = document[pageNumber - 1];
                var words = page.Words;
                var dividend = words.Where(w => w.Text == "Dividends").ToList();
                foreach (var anchor in dividend)
                {
                    var y = anchor.Bottom;
                    var header = words.Where(w => Math.Abs(w.Bottom - y) < 5).ToList();
                    if (!header.Any(w => w.Text == "Account") || !header.Any(w => w.Text == "Interest") || !header.Any(w => w.Text == "Other")) continue;
                    var hasBalance = header.Any(w => w.Text == "Balance"); var hasGross = header.Any(w => w.Text == "Gross");
                    var amountCount = 3 + (hasBalance ? 1 : 0) + (hasGross ? 1 : 0);
                    var letters = page.Letters.Where(l => l.BaselineY < y - 2 && l.BaselineY > y - 150).ToList();
                    // 按基线归行，而非 PDF 内部绘制顺序；重叠加粗文字按坐标和值去重。
                    var lines = letters.GroupBy(l => Math.Round(l.BaselineY / 2) * 2).OrderByDescending(g => g.Key)
                        .Select(g => string.Concat(g.GroupBy(l => (Math.Round(l.X, 1), l.Value)).Select(v => v.First()).OrderBy(l => l.X).Select(l => l.Value))).ToList();
                    var accountParts = new List<string>(); var ordinal = 0;
                    foreach (var line in lines)
                    {
                        var amounts = Regex.Matches(line, @"(?:USD|HKD|CNY)\s*[+-]?\d[\d,]*(?:\.\d+)?");
                        if (amounts.Count == 0)
                        {
                            if (line.StartsWith('(') || line.StartsWith("Note", StringComparison.OrdinalIgnoreCase)) break;
                            if (line.Trim() != "") accountParts.Add(line.Trim());
                            continue;
                        }
                        if (amounts.Count != amountCount) throw new CrsException($"PDF 第 {pageNumber} 页收入行金额列不完整，请使用 Excel 或核对原表。");
                        var namePart = line[..amounts[0].Index].Trim(); if (namePart != "") accountParts.Add(namePart);
                        var name = string.Join(" ", accountParts); accountParts.Clear();
                        if (name == "") throw new CrsException("PDF 收入行缺少账户名称。");
                        var offset = hasBalance ? 1 : 0;
                        var values = new Dictionary<string, string> { ["Account"] = name,
                            ["Dividends"] = Prefix(amounts[offset].Value), ["Interest"] = Prefix(amounts[offset + 1].Value), ["Other income"] = Prefix(amounts[^1].Value) };
                        result.Add(new($"PDF第{pageNumber}页", ++ordinal, values));
                    }
                }
            }
            if (result.Count == 0) result.AddRange(ReadChinese(document, cancellation));
            if (result.Count == 0) throw new CrsException("PDF 未识别到可验证的账户收入表；扫描件或未知布局请改用富途 Excel。");
            return result;
        }
        catch (Exception e) when (e is not (CrsException or OperationCanceledException)) { throw new CrsException("富途 PDF 读取失败，请检查文件是否损坏、加密或为扫描件。"); }
    }
    /// <summary>为紧邻数字的币种补分隔空格，共用收入金额解析规则。</summary>
    private static string Prefix(string value) => value[..3] + " " + value[3..].Trim();

    /// <summary>迁移原版带牛牛号、年份和全年收入列的中文税表，按表头坐标分列。</summary>
    private static List<FutuRow> ReadChinese(IReadOnlyList<PdfPageContent> document, CancellationToken cancellation)
    {
        var result = new List<FutuRow>();
        foreach (var page in document)
        {
            cancellation.ThrowIfCancellationRequested();
            var lines = page.Letters.GroupBy(l => Math.Round(l.BaselineY / 2) * 2).OrderByDescending(g => g.Key).ToList();
            for (var index = 0; index < lines.Count; index++)
            {
                var header = lines[index].OrderBy(l => l.X).ToList();
                var text = string.Concat(header.Select(l => l.Value));
                var required = new[] { "牛牛号", "账户名称", "全年股息", "全年利息", "全年其他收入" };
                if (!required.All(text.Contains)) continue;
                var fields = required.Concat(["年份", "币种"]).Where(text.Contains).Select(f => {
                    var charIndex = text.IndexOf(f, StringComparison.Ordinal); var offset = 0; var first = 0; var last = header.Count - 1;
                    for (var i = 0; i < header.Count; i++) { if (offset <= charIndex) first = i; if (offset < charIndex + f.Length) last = i; offset += header[i].Value.Length; }
                    return (Name: f, Left: header[first].X, Right: header[last].Right);
                }).OrderBy(f => f.Left).ToArray();
                var rowNumber = 0;
                for (var rowIndex = index + 1; rowIndex < lines.Count; rowIndex++)
                {
                    var cells = new Dictionary<string, string>(); var letters = lines[rowIndex].OrderBy(l => l.X).ToList();
                    for (var c = 0; c < fields.Length; c++)
                    {
                        var left = c == 0 ? double.NegativeInfinity : (fields[c - 1].Right + fields[c].Left) / 2;
                        var right = c == fields.Length - 1 ? double.PositiveInfinity : (fields[c].Right + fields[c + 1].Left) / 2;
                        cells[fields[c].Name] = string.Concat(letters.Where(l => l.X >= left && l.X < right).Select(l => l.Value)).Trim();
                    }
                    if (!Regex.IsMatch(cells["牛牛号"], @"^\d+$")) break;
                    if (cells["账户名称"] == "") throw new CrsException("PDF 中文收入行账户名称缺失。");
                    result.Add(new($"PDF第{page.Number}页", ++rowNumber, cells));
                }
            }
        }
        return result;
    }
}
