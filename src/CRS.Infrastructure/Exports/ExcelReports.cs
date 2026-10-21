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
    public static void Export(string path, CalculationResult r, IEnumerable<ReviewNote>? reviews = null,
        CancellationToken cancellation = default,Action<Action>? commitGuard=null)
    {
        // 构建工作簿前检查原会话，保持明文导出的锁定拒绝行为。
        cancellation.ThrowIfCancellationRequested(); commitGuard?.Invoke(()=>cancellation.ThrowIfCancellationRequested());
        WriteWorkbook(r,reviews,cancellation,write=>AtomicExport.Write(path,write,cancellation,commitGuard));
    }
    /// <summary>加密导出直接写入内存，不先创建明文工作簿文件。</summary>
    internal static void Write(Stream destination,CalculationResult r,IEnumerable<ReviewNote>? reviews,CancellationToken cancellation)
        =>WriteWorkbook(r,reviews,cancellation,write=>write(destination));
    private static void WriteWorkbook(CalculationResult r,IEnumerable<ReviewNote>? reviews,CancellationToken cancellation,Action<Action<Stream>> save)
    {
        cancellation.ThrowIfCancellationRequested();
        // 直接调用导出适配器也需校验，不能依赖界面先加载过历史记录。
        RunEvidenceSeal.Verify(r); r.Freeze();
        using var workbook = new XSSFWorkbook();
        var header = workbook.CreateCellStyle(); var font = workbook.CreateFont(); font.IsBold = true; header.SetFont(font);
        var number = workbook.CreateCellStyle(); number.DataFormat = workbook.CreateDataFormat().GetFormat("0.00########");
        // 创建固定表头工作表，将外部字符串强制写为文本，金额仅在输出阶段转换。
        void Sheet(string name, string[] titles, IEnumerable<object?[]> rows)
        {
            // 每个工作表和数据行都响应取消，避免大量明细继续构建。
            cancellation.ThrowIfCancellationRequested();
            var sheet = workbook.CreateSheet(name); var top = sheet.CreateRow(0);
            for (int i = 0; i < titles.Length; i++) { var cell = top.CreateCell(i); cell.SetCellValue(titles[i]); cell.CellStyle = header; sheet.SetColumnWidth(i, Math.Min(i == 1 ? 48 : 24, 80) * 256); }
            int index = 1;
            foreach (var values in rows)
            {
                cancellation.ThrowIfCancellationRequested();
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
            ["规则版本", TaxEngine.PolicyVersion], ["计算编号", r.SnapshotId], ["复算父任务", r.ParentSnapshotId], ["期末结转可用", r.CarryEligible],
            ["年度汇总", r.IsAnnualAggregate], ["来源任务数", r.AggregationSources.Count],
            ["输入恢复模式", r.InputRecoveryMode], ["规范化输入摘要", r.CanonicalInputDigest], ["可重放", r.IsReplayable],
            ["适用边界", "迁移现有税务辅助测算口径；抵免归属和实际申报规则仍需独立核实。"] });
        // 与界面共用金额语义；未知金额留空，说明明确区分空白、真实零值与公式核对值。
        Sheet("税务汇总", ["项目", "人民币金额", "说明"], TaxSummaryPresentation.Rows(r).Select(row=>new object?[] {row.项目,row.人民币金额,row.说明}));
        // 不给旧记录补造历史执行身份；新底稿披露真实封存版本和摘要。
        Sheet("运行证据", ["项目", "内容"], r.EvidenceSeal is { } seal ? new object?[][] {
            ["编码版本",seal.Encoding],["Application SHA256",seal.ApplicationSha256],["Domain SHA256",seal.DomainSha256],
            ["Infrastructure SHA256",seal.InfrastructureSha256],["业务载荷摘要",seal.PayloadDigest],["运行证据摘要",seal.EvidenceDigest],
            ["边界","一致性摘要，不是数字签名或券商来源认证；本工作簿不含程序副本，新任务的程序文件由加密库另存，旧记录可能无归档。"] }
            : new object?[][] { ["封存状态","旧记录无运行封存，不推定历史执行构建。"] });
        // 汇总底稿披露全部来源任务及券商账户关系，原账户底稿仍可独立追溯。
        if (r.IsAnnualAggregate)
            Sheet("年度汇总来源", ["来源任务", "券商", "年度", "账户"], r.AggregationSources.SelectMany(s => s.Accounts
                .Select(a => new object?[] { s.SnapshotId, s.Broker, s.Year, a })));
        Sheet("FIFO明细", ["账户", "证券", "币种", "买入日期", "卖出日期", "数量", "已含费总成本", "收入", "买入费用披露", "卖出费用", "收益", "人民币收益", "成本口径", "买入ID", "卖出ID", "买入文件", "卖出文件", "券商"],
            r.Matches.Select(m => new object?[] { m.Key.Account, m.Symbol, m.Key.Currency, m.BuyDate, m.SellDate, m.Quantity, m.Cost, m.Revenue, m.BuyFee, m.SellFee, m.Gain, m.GainCny, m.CostBasisMode, m.BuyId, m.SellId, m.BuyFile, m.SellFile, m.Key.Broker }));
        if (r.ForeignCreditAssessment is { } credit)
        {
            // 国家限额、所得归属和凭证摘要随同税务底稿导出，排除税款单独披露。
            Sheet("抵免国家限额", ["国家地区", "收入人民币", "辅助限额", "可核对税款", "排除税款", "辅助抵免", "超限未结转"],
                credit.Countries.Select(c => new object?[] { c.Country, c.IncomeCny, c.LimitCny, c.PaidCny, c.ExcludedCny, c.CreditCny, c.ExcessCny }));
            Sheet("抵免所得分配", ["券商", "账户", "国家地区", "所得项目", "币种", "原币金额"],
                credit.Evidence.Income.Select(i => new object?[] { i.Broker, i.Account, i.Country, i.Category, i.Currency, i.Amount }));
            Sheet("抵免凭证关系", ["券商", "账户", "国家地区", "所得项目", "币种", "原币税款", "征税机关", "凭证编号", "排除原因", "凭证类型", "文件名", "SHA256"],
                credit.Evidence.Payments.SelectMany(t => t.Proofs.Select(p => new object?[] { t.Broker, t.Account, t.Country, t.Category, t.Currency, t.Amount, t.Authority, t.Reference, t.Exclusion, p.Role, p.Name, p.Sha256 })));
        }
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
        Sheet("解析版本", ["原件SHA256", "批次ID", "券商", "来源读取版本", "税务投影版本"],
            r.Sources.Select(s=>s.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Select(hash=>
            {
                var p=r.ImportProvenance?.SingleOrDefault(p=>p.SourceFileHash.Equals(hash,StringComparison.OrdinalIgnoreCase));
                return new object?[]{hash,p?.ImportBatchId,p?.Broker,p?.ReaderVersion??"未记录",p?.ProjectionVersion??"未记录"};
            }));
        Sheet("人工复核", ["计算编号", "类型", "依据", "确认时间（北京时间）"], (reviews ?? []).Select(v => new object?[] { v.SnapshotId, v.Category, v.Evidence, CRS.Timing.BeijingClock.NormalizeText(v.CreatedUtc) }));
        // 生命周期资料跟随底稿，完整活动期与完整自然年必须明确区分。
        if(r.AccountPeriods is not null) Sheet("账户期间依据",["账户","开户日期","关闭日期","开户零持仓已核对","凭证","凭证摘要","用户已核对"],
            r.AccountPeriods.Select(p=>new object?[]{p.Account,p.Opened,p.Closed?.ToString()??"尚未关闭",p.OpeningZeroConfirmed,p.EvidenceName,p.EvidenceSha256,p.Reviewed}));
        if(r.AccountCoverage is not null) Sheet("期间覆盖状态",["账户","需核对起日","需核对止日","覆盖类型","期间完整（非税额签认）"],
            r.AccountCoverage.Select(p=>new object?[]{p.Account,p.Start,p.End,p.Kind=="Provisional"?"临时报告":p.Kind=="AccountActivity"?"账户活动期":"自然年",p.PeriodComplete}));
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
        save(output=>workbook.Write(output,true));
    }
}
