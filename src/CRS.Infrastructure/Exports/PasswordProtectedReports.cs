using System.Globalization;
using System.Security.Cryptography;
using CRS.Application.Abstractions;
using CRS.Documents.Excel;
using CRS.Documents.Pdf;
using CRS.Security.Cryptography;

namespace CRS.Infrastructure;

/// <summary>协调报税内容和原子提交；文档生成、格式加密和密码长度校验均使用 CRS.Documents。</summary>
internal static class PasswordProtectedReports
{
    public static void Export(string path, CalculationResult result, TaxDocumentFormat format, string password,
        IEnumerable<ReviewNote>? reviews = null, CancellationToken cancellation = default, Action<Action>? guard = null)
    {
        cancellation.ThrowIfCancellationRequested(); guard?.Invoke(() => cancellation.ThrowIfCancellationRequested());
        if (format is not (TaxDocumentFormat.Excel or TaxDocumentFormat.Pdf)) throw new CrsException("不支持此报税文档格式。");
        try { PasswordEncryption.ValidatePassword(password); }
        catch (CryptographicException) { throw new CrsException("文档加密密码需为 12—1024 个字符，请在设置中修改。"); }
        try {if(format==TaxDocumentFormat.Excel)ExcelWriter.ValidatePassword(password);else PdfTextWriter.ValidatePassword(password);}
        catch(ArgumentException error) {throw new CrsException(error.Message);}
        var extension = format == TaxDocumentFormat.Excel ? ".xlsx" : ".pdf";
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new CrsException("请选择 " + extension + " 格式保存文件。");
        RunEvidenceSeal.Verify(result); result.Freeze();
        if (format == TaxDocumentFormat.Excel)
        {
            ExcelReports.ExportPasswordProtected(path,result,password,reviews,cancellation,guard);
            return;
        }
        AtomicExport.Write(path,stream=>PdfTextWriter.WritePasswordProtected(stream,Paragraphs(result,reviews),password,cancellation),cancellation,guard);
    }

    /// <summary>复用 Excel 与界面的金额语义；PDF 是摘要，不补算未知税额，也不替代完整底稿。</summary>
    private static IEnumerable<PdfParagraph> Paragraphs(CalculationResult r, IEnumerable<ReviewNote>? reviews)
    {
        yield return new("小财牛 · 税务核算摘要", true);
        yield return new($"年度：{r.Year}    券商：{r.Broker}    计算编号：{r.SnapshotId}");
        yield return new($"账户：{string.Join("、", r.CoveredAccounts)}");
        yield return new($"计算状态：{ResultLabels.Calculation(r.CalculationStatus)}；资料：{ResultLabels.Completeness(r.DataCompleteness)}；对账：{ResultLabels.Reconciliation(r.ReconciliationStatus)}；用途：{ResultLabels.Usage(r.UsageLabel)}");
        yield return new("税务汇总（人民币）", true);
        foreach (var row in TaxSummaryPresentation.Rows(r))
        {
            yield return new(row.项目 + "：" + (row.人民币金额?.ToString("N2", CultureInfo.InvariantCulture) ?? "不可确定"));
            yield return new(row.说明);
        }
        yield return new("适用说明", true);
        yield return new("本文件为冻结结果的辅助摘要；FIFO、原件摘要、汇率、抵免凭证和结转请结合完整 Excel 底稿复核，实际申报口径仍需独立核实。");
        if (r.IsAnnualAggregate)
            foreach (var source in r.AggregationSources)
                yield return new($"汇总来源：{source.SnapshotId}；{source.Broker}；{source.Year}；{string.Join("、", source.Accounts)}");
        foreach (var issue in r.Issues) yield return new($"待复核 [{issue.Code}]：{issue.Message}；账户：{issue.Account}；证券：{issue.Symbol}");
        foreach (var warning in r.Warnings) yield return new("提示：" + warning);
        foreach (var note in reviews ?? []) yield return new($"人工复核 [{note.Category}]：{note.Evidence}；{CRS.Timing.BeijingClock.NormalizeText(note.CreatedUtc)}");
    }

}
