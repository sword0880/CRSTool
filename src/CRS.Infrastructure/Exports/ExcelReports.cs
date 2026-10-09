using System.Collections;
using System.Globalization;
using CRS.Domain;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace CRS.Infrastructure;

public static class ExcelReports
{
    // 读写统一使用 NPOI；读取时不执行公式或外部工作簿链接。
    /// <summary>使用 NPOI 限量读取指定工作表文本，不执行公式或外部链接。</summary>
    public static List<List<string>> ReadSheet(string path, string sheetName)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new CrsException("Excel 文件不能超过 25 MB。");
        using var input = File.OpenRead(path); using var workbook = WorkbookFactory.Create(input);
        var sheet = workbook.GetSheet(sheetName) ?? throw new CrsException("Excel 中没有指定工作表。");
        if (sheet.LastRowNum > 100000) throw new CrsException("工作表记录过多。");
        var formatter = new DataFormatter(CultureInfo.InvariantCulture); var rows = new List<List<string>>();
        foreach (IRow row in sheet)
        {
            if (row.LastCellNum > 1000) throw new CrsException("工作表列数过多。");
            rows.Add(Enumerable.Range(0, Math.Max(0, (int)row.LastCellNum)).Select(i => formatter.FormatCellValue(row.GetCell(i))).ToList());
        }
        return rows;
    }
    /// <summary>使用 NPOI 导出计算、复核、来源及快照，缺失金额保留空白。</summary>
    public static void Export(string path, CalculationResult r, IEnumerable<ReviewNote>? reviews = null)
    {
        using var workbook = new XSSFWorkbook();
        var header = workbook.CreateCellStyle(); var font = workbook.CreateFont(); font.IsBold = true; header.SetFont(font);
        var number = workbook.CreateCellStyle(); number.DataFormat = workbook.CreateDataFormat().GetFormat("0.00########");
        // 创建固定表头工作表，将外部字符串强制写为文本，金额仅在输出阶段转换。
        void Sheet(string name, string[] titles, IEnumerable<object?[]> rows)
        {
            var sheet = workbook.CreateSheet(name); var top = sheet.CreateRow(0);
            for (int i = 0; i < titles.Length; i++) { var cell = top.CreateCell(i); cell.SetCellValue(titles[i]); cell.CellStyle = header; sheet.SetColumnWidth(i, Math.Min(i == 1 ? 48 : 24, 80) * 256); }
            int index = 1;
            foreach (var values in rows)
            {
                var row = sheet.CreateRow(index++);
                for (int i = 0; i < values.Length; i++)
                {
                    var cell = row.CreateCell(i); var v = values[i];
                    if (v is decimal d) { cell.SetCellValue((double)d); cell.CellStyle = number; }
                    else if (v is int n) cell.SetCellValue(n);
                    // 始终写文本，避免证券名或人工说明以等号开头被解释为公式。
                    else cell.SetCellValue(v?.ToString() ?? "");
                }
            }
            sheet.CreateFreezePane(0, 1);
        }
        Sheet("计算说明", ["项目", "内容"], new object?[][] {
            ["年度", r.Year], ["券商", r.Broker], ["计算状态", ResultLabels.Calculation(r.CalculationStatus)], ["数据完整性", ResultLabels.Completeness(r.DataCompleteness)],
            ["对账状态", ResultLabels.Reconciliation(r.ReconciliationStatus)], ["使用标签", ResultLabels.Usage(r.UsageLabel)], ["可作为完整结果", r.Complete],
            ["纳税人范围", r.TaxpayerScopeId], ["纳入账户", string.Join("、", r.CoveredAccounts)], ["临时测算", r.Provisional],
            ["规则版本", TaxEngine.PolicyVersion], ["计算编号", r.SnapshotId], ["期末结转可用", r.CarryEligible],
            ["输入恢复模式", r.InputRecoveryMode], ["规范化输入摘要", r.CanonicalInputDigest], ["可重放", r.IsReplayable],
            ["适用边界", "迁移现有税务辅助测算口径；抵免归属和实际申报规则仍需独立核实。"] });
        Sheet("税务汇总", ["项目", "人民币金额"], new object?[][] { ["股息", r.Summary.DividendCny], ["利息", r.Summary.InterestCny],
            ["资本收益", r.Summary.GainCny], ["股息利息税", r.Summary.DividendInterestTax], ["资本收益税", r.Summary.GainTax], ["境外抵免", r.Summary.ForeignCredit],
            ["辅助测算补税（原始公式）", r.Summary.SupplementTax], ["年度预计补税（状态允许时）", r.EstimatedTopUpCny] });
        Sheet("FIFO明细", ["账户", "证券", "币种", "买入日期", "卖出日期", "数量", "已含费总成本", "收入", "买入费用披露", "卖出费用", "收益", "人民币收益", "成本口径", "买入ID", "卖出ID", "买入文件", "卖出文件"],
            r.Matches.Select(m => new object?[] { m.Key.Account, m.Symbol, m.Key.Currency, m.BuyDate, m.SellDate, m.Quantity, m.Cost, m.Revenue, m.BuyFee, m.SellFee, m.Gain, m.GainCny, m.CostBasisMode, m.BuyId, m.SellId, m.BuyFile, m.SellFile }));
        Sheet("待复核", ["代码", "说明", "账户", "证券", "币种", "日期", "原始ID", "来源文件"], r.Issues.Select(i => new object?[] { i.Code, i.Message, i.Account, i.Symbol, i.Currency, i.Date, i.RecordId, i.File }));
        Sheet("现金余额对账", ["账户", "币种", "起日", "止日", "期初现金", "明细变动", "计算期末", "报告期末", "差额", "状态", "说明", "文件"],
            r.CashChecks.Select(c => new object?[] { c.Account, c.Currency, c.Start, c.End, c.Starting, c.Movement, c.CalculatedEnding, c.ReportedEnding, c.Difference, c.Status, c.Reason, c.File }));
        Sheet("卖出收益对账", ["账户", "证券", "币种", "成交ID", "计算收益", "券商收益", "差额", "状态"],
            r.PnlChecks.Select(p => new object?[] { p.Account, p.Symbol, p.Currency, p.TradeId, p.Calculated, p.Reported, p.Difference, p.Status }));
        Sheet("汇率底稿", ["年度", "币种", "汇率", "来源", "网址", "方法", "起日", "止日", "临时", "来源确认"],
            r.Rates.Select(v => new object?[] { v.Year, v.Currency, v.Value, v.Source, v.Url, v.Method, v.Start, v.End, v.Provisional, v.SourceConfirmed }));
        Sheet("期末LOT", ["账户", "证券标识", "证券", "币种", "买入时间", "数量", "剩余成本", "剩余费用", "原始ID", "来源"],
            r.EndingLots.Select(l => new object?[] { l.Key.Account, l.Key.Instrument, l.Symbol, l.Key.Currency, l.BuyTime, l.Quantity, l.Cost, l.Fee, l.RecordId, l.SourceFile }));
        Sheet("来源文件", ["文件", "SHA256", "账户", "起日", "止日", "期初", "用途", "权威级别"], r.Sources.Select(s => new object?[] { s.File, s.Sha256, s.Account, s.Start, s.End, s.Opening, s.Purpose, s.Authority }));
        Sheet("人工复核", ["计算编号", "类型", "依据", "确认时间UTC"], (reviews ?? []).Select(v => new object?[] { v.SnapshotId, v.Category, v.Evidence, v.CreatedUtc }));
        if (r.Broker == "FUTU")
        {
            Sheet("资本收益汇总", ["账户", "证券", "市场", "币种", "匹配笔数", "已确定收益（原币）", "已确定收益（人民币）"],
                FutuReporting.Capital(r).Select(g => new object?[] { g.Account, g.Symbol, g.Market, g.Currency, g.Count, g.Gain, g.GainCny }));
            Sheet("入金汇总", ["账户", "币种", "笔数", "入金金额"], FutuReporting.Deposits(r).Select(g => new object?[] { g.Account, g.Currency, g.Count, g.Amount }));
            Sheet("现金派息汇总", ["账户", "证券", "币种", "笔数", "收到金额（仅供核对）"], FutuReporting.DividendReceipts(r).Select(g => new object?[] { g.Account, g.Symbol, g.Currency, g.Count, g.Amount }));
            Sheet("年度收入主表", ["账户", "账户名称", "年度", "币种", "股息", "利息", "其他收入（未计税）", "文件", "工作表", "原始行号"],
                r.AnnualIncome.Select(i => new object?[] { i.Account, i.AccountName, i.Year, i.Currency, i.Dividend, i.Interest, i.OtherIncome, i.File, i.Sheet, i.Row }));
            Sheet("成交金额核对", ["账户", "证券", "币种", "成交ID", "数量价格金额", "原成交金额", "应变动金额", "原变动金额", "状态", "文件", "工作表", "原始行号"],
                r.TradeAmountChecks.Select(c => new object?[] { c.Account, c.Symbol, c.Currency, c.TradeId, c.ExpectedGross, c.ReportedGross, c.ExpectedNet, c.ReportedNet, c.Status, c.File, c.Sheet, c.Row }));
            Sheet("资金进出明细", ["账户", "账户名称", "日期", "币种", "类型", "方向", "金额", "备注", "证券", "文件", "工作表", "原始行号"],
                r.FundMovements.Select(f => new object?[] { f.Account, f.AccountName, f.Date, f.Currency, f.Type, f.Direction, f.Amount, f.Description, f.Symbol, f.File, f.Sheet, f.Row }));
            Sheet("证券资产事件", ["账户", "证券", "市场", "币种", "日期", "类型", "方向", "数量", "备注", "文件", "工作表", "原始行号"],
                r.SecurityMovements.Select(s => new object?[] { s.Key.Account, s.Symbol, s.Key.Market, s.Key.Currency, s.Date, s.Type, s.Direction, s.Quantity, s.Description, s.File, s.Sheet, s.Row }));
        }
        Sheet("提示", ["说明"], r.Warnings.Select(w => new object?[] { w }));
        // Excel 单元格限制为 32767 字符；长快照分段存放，禁止静默截断。
        Sheet("计算快照", ["段号", "JSON文本"], Enumerable.Range(0, (r.SnapshotJson.Length + 29999) / 30000).Select(i => new object?[] { i + 1, r.SnapshotJson.Substring(i * 30000, Math.Min(30000, r.SnapshotJson.Length - i * 30000)) }));
        using var output = File.Create(path); workbook.Write(output, true);
    }
}
