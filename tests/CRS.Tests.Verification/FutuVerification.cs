using CRS.Domain;
using CRS.Infrastructure;
using NPOI.XSSF.UserModel;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

/// <summary>富途边界与独立算式验证；全部常规测试使用脱敏合成资料。</summary>
internal static class FutuVerification
{
    /// <summary>按显式提供的本地路径核对真实模板，仅输出验证结论，不复制账户资料。</summary>
    public static void VerifySamples(Action<string, Action> check, string directory, string? referencePath = null)
    {
        check("FUTU-REAL-001 2021 Excel 实际字段与记录范围", () => {
            var data = Sample(directory, 2021);
            Assert(data.Trades.Count > 0 && data.AnnualIncome.Count > 0 && data.FundMovements.Count > 0 && data.SecurityMovements.Count == 3);
            Assert(data.Trades.All(t => t.Key.Broker == "FUTU" && t.Key.Account != "" && t.Key.Market != "") && data.TradeAmountChecks.Count == data.Trades.Count);
        });
        check("FUTU-REAL-002 三语 PDF 仅计英文收入一次", () => {
            var data = new FutuImporter(2025).Parse([Directory.GetFiles(directory, "2025*.pdf").Single()]);
            Assert(data.AnnualIncome.Count == 1 && data.AnnualIncome[0].Sheet == "PDF第3页");
        });
        check("FUTU-REAL-003 不依赖错误行数元数据读取年度工作簿", () => {
            foreach (var y in new[] { 2022, 2023, 2024, 2025 })
            {
                var d = Sample(directory, y);
                Assert(d.Trades.Count > 0 && d.AnnualIncome.Count > 0);
            }
        });
        if (referencePath is not null)
            check("FUTU-REAL-004 五个年度逐行对照 Python 原版", () => CompareReference(directory, referencePath));
        check("FUTU-REAL-005 五年度真实模板可完成统一计算入口", () => {
            foreach (var y in Enumerable.Range(2021, 5))
            {
                var activity = Directory.GetFiles(directory, $"{y}_*.xlsx").Single();
                var income = Directory.GetFiles(directory, $"{y}*股息.xlsx").SingleOrDefault() ?? Directory.GetFiles(directory, $"{y}*.pdf").Single();
                var r = CalculationServices.Create(new VerificationRates(), "FUTU", y).Calculate([income, activity], y, true, true);
                // 固定合成汇率只检验入口与资料状态；该测试不输出真实人民币税额或签认业务成本。
                Assert(r.Broker == "FUTU" && r.AnnualIncome.Count > 0 && r.TradeAmountChecks.Count > 0 && r.TaxInputs?.Broker == "FUTU"
                    && !r.Complete && r.EstimatedTopUpCny is null);
            }
        });
    }
    /// <summary>从显式授权的样本目录寻找唯一年度来源，不在代码中硬编码用户身份文件名。</summary>
    private static ImportData Sample(string directory, int year)
    {
        var activity = Directory.GetFiles(directory, $"{year}_*.xlsx").Single();
        var income = Directory.GetFiles(directory, $"{year}*股息.xlsx").SingleOrDefault() ?? Directory.GetFiles(directory, $"{year}*.pdf").Single();
        return new FutuImporter(year).Parse([income, activity]);
    }
    /// <summary>核对每个原始行的方向、数量、单价、总金额和费用，以及年度收入和净扣税事实。</summary>
    private static void CompareReference(string directory, string referencePath)
    {
        using var reference = System.Text.Json.JsonDocument.Parse(File.ReadAllText(referencePath));
        foreach (var year in Enumerable.Range(2021, 5))
        {
            var data = Sample(directory, year); var expected = reference.RootElement.GetProperty(year.ToString());
            var trades = expected.GetProperty("trades").EnumerateArray().ToList(); Assert(trades.Count == data.Trades.Count);
            foreach (var e in trades)
            {
                var t = data.Trades.Single(t => t.Row == e.GetProperty("row").GetInt32());
                Assert(t.Side == e.GetProperty("side").GetString() && t.Quantity == Number(e, "quantity") && t.Price == Number(e, "price")
                    && t.Gross == Number(e, "gross") && t.Fee == Number(e, "fee") && t.Net == Number(e, "net") && t.Key.Currency == e.GetProperty("currency").GetString());
                Assert(t.Time.DateTime == DateTime.Parse(e.GetProperty("time").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            }
            var income = expected.GetProperty("income").EnumerateArray().ToList(); Assert(income.Count == data.AnnualIncome.Count);
            foreach (var currency in income.Select(i => i.GetProperty("currency").GetString()).Distinct())
                Assert(data.AnnualIncome.Where(i => i.Currency == currency).Sum(i => i.Dividend) == income.Where(i => i.GetProperty("currency").GetString() == currency).Sum(i => Number(i, "dividend"))
                    && data.AnnualIncome.Where(i => i.Currency == currency).Sum(i => i.Interest) == income.Where(i => i.GetProperty("currency").GetString() == currency).Sum(i => Number(i, "interest"))
                    && data.AnnualIncome.Where(i => i.Currency == currency).Sum(i => i.OtherIncome) == income.Where(i => i.GetProperty("currency").GetString() == currency).Sum(i => Number(i, "other")));
            Assert(data.Cash.Count(c => c.Type == "Withholding Tax") == expected.GetProperty("withholdingCount").GetInt32()
                && -data.Cash.Where(c => c.Type == "Withholding Tax").Sum(c => c.Amount) == Number(expected, "paidTax")
                && data.FundMovements.Where(f => f.Direction == "In" && f.Type == "出入金").Sum(f => f.Amount) == Number(expected, "deposits"));
        }
        /// <summary>读取参考文件中的十进制文本，禁止先转换为浮点数。</summary>
        static decimal Number(System.Text.Json.JsonElement element, string name) => decimal.Parse(element.GetProperty(name).GetString()!, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }
    /// <summary>注册富途交易、现金、收入及 PDF 验证，不依赖用户私有样本。</summary>
    public static void Run(Action<string, Action> check, string workspace, ExchangeRates rates)
    {
        var calculator = CalculationServices.Create(rates, "FUTU", 2025);
        check("FUTU-001 Excel 端到端费用及小批次金额守恒", () => {
            var pair = Files(workspace); var r = calculator.Calculate(pair, 2025, true, true, saveCanonicalSnapshot: true);
            Assert(r.Matches.Count == 2 && r.Matches.Sum(m => m.Revenue) == 1.01m && r.Matches.Sum(m => m.Cost) == .68m && r.Matches.Sum(m => m.SellFee) == .03m && r.Summary.GainCny == .30m);
            Assert(r.Summary.DividendCny == 100 && r.Summary.InterestCny == 5 && r.Summary.ForeignCredit == 8);
            Assert(r.TradeAmountChecks.All(c => c.Status == "一致") && r.TradeAmountChecks[0].Row == 2);
            Assert(r.Broker == "FUTU" && !r.Complete && r.EstimatedTopUpCny is null && r.ReconciliationStatus == ReconciliationStatus.NotVerifiable);
            var replay = calculator.Replay(r); Assert(replay.Summary == r.Summary && replay.FundMovements.Count == r.FundMovements.Count && replay.TradeAmountChecks.Count == 3);
            var store = new LocalStore(Path.Combine(workspace, "futu-db")); store.Save(r); Assert(store.Load(r.SnapshotId).AnnualIncome.Count == 1);
            var report = Path.Combine(workspace, "futu-report.xlsx"); ExcelReports.Export(report, r);
            Assert(ExcelReports.ReadSheet(report, "年度收入主表").Count == 2 && ExcelReports.ReadSheet(report, "成交金额核对").Count == 4);
        });
        check("FUTU-002 收入主表与派息明细不重复计税", () => {
            var r = calculator.Calculate(Files(workspace), 2025, true, true);
            Assert(r.Summary.DividendCny == 100 && r.FundMovements.Any(f => f.Type.Contains("公司行动")));
        });
        check("FUTU-003 原始金额异常保留待复核", () => {
            var pair = Files(workspace, gross: "1.50"); var r = calculator.Calculate(pair, 2025, true, true);
            Assert(r.Issues.Any(i => i.Code == "TRADE_AMOUNT_MISMATCH") && !r.Complete && r.EstimatedTopUpCny is null);
        });
        check("FUTU-004 纯利息无币种列仍可识别", () => {
            var pair = Files(workspace, currency: false, dividend: "-", interest: "USD 5");
            var r = calculator.Calculate(pair, 2025, true, true); Assert(r.Summary.InterestCny == 5 && r.Summary.DividendCny == 0);
        });
        check("FUTU-005 拒绝小数年份和冲突币种", () => {
            Reject(() => calculator.Calculate(Files(workspace, year: "2025.5"), 2025, true, true));
            Reject(() => calculator.Calculate(Files(workspace, interest: "HKD 5"), 2025, true, true));
        });
        check("FUTU-006 同年度多来源不能按相同行值去重", () => {
            var a = Files(workspace); var b = Files(workspace);
            Reject(() => calculator.Calculate([a[0], a[1], b[1]], 2025, true, true));
            Reject(() => calculator.Calculate([a[0], a[0], a[1]], 2025, true, true));
        });
        check("FUTU-007 期初市价不能替代成本及无持仓冲突", () => {
            var r = calculator.Calculate(Files(workspace, openingQuantity: "1"), 2025, true, true);
            Assert(r.Matches.Count == 0 && r.Issues.Any(i => i.Code == "MISSING_COST") && r.Issues.Any(i => i.Code == "OPENING_CONFLICT") && !r.CarryEligible);
        });
        check("FUTU-008 证券转入和更名不能补造零成本", () => {
            var r = calculator.Calculate(Files(workspace, assetEvent: true), 2025, true, true);
            Assert(r.Matches.Count == 0 && r.SecurityMovements.Count == 1 && r.Issues.Any(i => i.Code == "UNSUPPORTED_TRANSFER") && !r.CarryEligible);
        });
        check("FUTU-009 缺交易只输出收入测算", () => {
            var r = calculator.Calculate([Files(workspace)[0]], 2025, true, true);
            Assert(r.Summary.DividendCny == 100 && r.Issues.Any(i => i.Code == "MISSING_TRADE_FILE") && r.DataCompleteness == DataCompleteness.Incomplete && r.EstimatedTopUpCny is null);
        });
        check("FUTU-010 缺字段、NaN、公式均拒绝", () => {
            Reject(() => calculator.Calculate(Files(workspace, missingPrice: true), 2025, true, true));
            Reject(() => calculator.Calculate(Files(workspace, dividend: "NaN"), 2025, true, true));
            Reject(() => calculator.Calculate(Files(workspace, formula: true), 2025, true, true));
        });
        check("FUTU-011 缺总费用列不能静默漏费", () => {
            var r = calculator.Calculate(Files(workspace, missingFee: true), 2025, true, true);
            Assert(r.Issues.Any(i => i.Code == "MISSING_TRADE_FEES") && r.EstimatedTopUpCny is null);
        });
        check("FUTU-012 PDF 金额列与独立人工表一致", () => {
            var path = Pdf(workspace); var data = new FutuImporter(2025).Parse([path]);
            Assert(data.AnnualIncome.Count == 1 && data.AnnualIncome[0].Dividend == 100 && data.AnnualIncome[0].Interest == 5 && data.AnnualIncome[0].OtherIncome == 0);
            Reject(() => calculator.Calculate([path, Files(workspace)[0]], 2025, true, true));
        });
        check("FUTU-013 取消导入不返回半成品", () => {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { new FutuImporter(2025).Parse(Files(workspace), cancellation: cts.Token); } catch (OperationCanceledException) { return; }
            throw new Exception("取消未生效");
        });
        check("FUTU-014 其他收入和净退税保留风险", () => {
            var r = calculator.Calculate(Files(workspace, other: "10", refund: "20"), 2025, true, true);
            Assert(r.Issues.Any(i => i.Code == "OTHER_INCOME") && r.Issues.Any(i => i.Code == "NET_TAX_REFUND") && r.Summary.ForeignCredit == 0);
        });
        check("FUTU-015 不跨市场或币种匹配成本", () => {
            foreach (var column in new[] { 4, 13 })
            {
                var pair = Files(workspace); Edit(pair[1], w => w.GetSheet("证券-交易流水").GetRow(3).GetCell(column).SetCellValue(column == 4 ? "SEHK" : "HKD"));
                var r = calculator.Calculate(pair, 2025, true, true); Assert(r.Matches.Count == 0 && r.Issues.Any(i => i.Code == "MISSING_COST"));
            }
        });
        check("FUTU-016 不跨账户匹配成本", () => {
            var pair = Files(workspace); Edit(pair[1], w => {
                var row = w.GetSheet("账户信息").CreateRow(2); row.CreateCell(0).SetCellValue("2025"); row.CreateCell(1).SetCellValue("OTHER"); row.CreateCell(2).SetCellValue("另一个账户");
                w.GetSheet("证券-交易流水").GetRow(3).GetCell(1).SetCellValue("OTHER"); });
            var r = calculator.Calculate(pair, 2025, true, true); Assert(r.Matches.Count == 0 && r.Issues.Any(i => i.Code == "MISSING_COST" && i.Account == "FUTU:OTHER"));
        });
        check("FUTU-017 原币余额闭环不冒充券商收益已核对", () => {
            var pair = Files(workspace); Edit(pair[1], w => Add(w, "证券-资金总览", ["时期类型", "日期", "账户号码", "币种", "金额"],
                [["期初", "20241231", "TEST", "USD", "0"], ["期末", "20251231", "TEST", "USD", "292.30"]]));
            var r = calculator.Calculate(pair, 2025, true, true); Assert(r.CashChecks.Single().Status == "一致" && !r.Complete && r.EstimatedTopUpCny is null);
            Assert(FutuReporting.Deposits(r).Single().Amount == 200 && FutuReporting.DividendReceipts(r).Single().Amount == 100);
        });
        check("FUTU-018 缺资金进出不能将税款视为已确认零", () => {
            var pair = Files(workspace); Edit(pair[1], w => w.RemoveSheetAt(w.GetSheetIndex("证券-资金进出")));
            var r = calculator.Calculate(pair, 2025, true, true); Assert(r.Issues.Any(i => i.Code == "MISSING_CASH_FLOWS") && r.DataCompleteness == DataCompleteness.Incomplete);
        });
        check("FUTU-019 只有表头不能证明年度零交易", () => {
            var pair = Files(workspace); Edit(pair[1], w => w.GetSheet("账户信息").RemoveRow(w.GetSheet("账户信息").GetRow(1)));
            Reject(() => calculator.Calculate(pair, 2025, true, true));
        });
        check("FUTU-020 富途真实结转成本替代期初市价", () => {
            var pair = Files(workspace, openingQuantity: "1"); Edit(pair[1], w => {
                var row = w.GetSheet("证券-持仓总览").CreateRow(2);
                var cells = new[] { "期末", "20251231", "证券", "TEST", "DEMO", "US", "USD", "1", "99999" };
                for (var i = 0; i < cells.Length; i++) row.CreateCell(i).SetCellValue(cells[i]); });
            var lot = new CostLot { Key = new("FUTU:TEST", "DEMO", "USD", "FUTU", "US"), Symbol = "DEMO", Quantity = 1, Cost = 10,
                BuyTime = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero), RecordId = "trusted-old-buy", SourceFile = "2024-source.xlsx", CostBasisMode = CostBasisMode.TotalCostIncludesFees };
            var carry = new CarryDocument(1, 2024, "trusted-2024", ["FUTU:TEST"], [lot], []);
            var r = calculator.Calculate(pair, 2025, false, true, carry: carry);
            Assert(r.Matches.Sum(m => m.Cost) == 10.34m && r.Matches.Sum(m => m.Gain) == -9.36m && r.CarryEligible && r.EndingLots.Single().Quantity == 1);
        });
        check("DB-DAPPER-001 查询映射、参数化文本及失败回滚", () => {
            var r = calculator.Calculate(Files(workspace), 2025, true, true); var db = new LocalStore(Path.Combine(workspace, "dapper-db")); db.Save(r);
            var text = "中文依据'); DROP TABLE calculations; --"; db.AddReview(r.SnapshotId, "核对", text);
            Assert(db.Reviews(r.SnapshotId).Single().Evidence == text && db.History().Single().Year == 2025 && !db.History().Single().Complete);
            try { db.Save(r); throw new Exception("重复主键未拒绝"); } catch (Microsoft.Data.Sqlite.SqliteException) { }
            Assert(db.History().Count == 1 && db.Load(r.SnapshotId).AnnualIncome.Count == 1);
        });
        check("FUTU-021 中文 PDF 固定表头与金额校验", () => {
            var path = ChinesePdf(workspace); if (path is null) throw new Exception("中文字体缺失，未完成中文 PDF 布局验收");
            var data = new FutuImporter(2025).Parse([path]); Assert(data.AnnualIncome.Single().Dividend == 100 && data.AnnualIncome.Single().Interest == 5 && data.AnnualIncome.Single().Currency == "USD");
        });
        check("FUTU-022 数值型碎股科学计数法不丢失", () => {
            var pair = Files(workspace, interest: "USD5"); Edit(pair[1], w => w.GetSheet("证券-交易流水").GetRow(1).GetCell(6).SetCellValue(.00000001));
            var data = new FutuImporter(2025).Parse(pair); Assert(data.Trades[0].Quantity == .00000001m && data.AnnualIncome.Single().Interest == 5);
        });
    }
    /// <summary>在测试副本中注入异常，不修改用户原始文件。</summary>
    private static void Edit(string path, Action<XSSFWorkbook> edit)
    {
        XSSFWorkbook workbook; using (var input = File.OpenRead(path)) workbook = new XSSFWorkbook(input);
        using (workbook) { edit(workbook); using var output = File.Create(path); workbook.Write(output, true); }
    }
    /// <summary>构造固定字段合成工作簿，可注入常见券商异常。</summary>
    private static string[] Files(string workspace, string year = "2025", bool currency = true, string dividend = "100", string interest = "5",
        string gross = "1.01", string openingQuantity = "0", bool assetEvent = false, bool missingPrice = false, bool missingFee = false,
        bool formula = false, string other = "0", string refund = "2")
    {
        var incomePath = Path.Combine(workspace, Guid.NewGuid() + "-2025-income.xlsx");
        using (var workbook = new XSSFWorkbook())
        {
            Add(workbook, "股息利息及其他收入", currency ? ["年份", "账户名称", "全年股息", "全年利息", "全年其他收入", "币种"] : ["年份", "账户名称", "全年股息", "全年利息", "全年其他收入"],
                [currency ? [year, "测试账户", dividend, interest, other, "USD"] : [year, "测试账户", dividend, interest, other]]);
            if (formula) workbook.GetSheetAt(0).GetRow(1).GetCell(2).SetCellFormula("1+1");
            using var output = File.Create(incomePath); workbook.Write(output, true);
        }
        var tradePath = Path.Combine(workspace, Guid.NewGuid() + ".xlsx");
        using (var workbook = new XSSFWorkbook())
        {
            Add(workbook, "账户信息", ["年份", "账户号码", "账户名称"], [["2025", "TEST", "测试账户"]]);
            var columns = new[] { "成交时间", "账户号码", "品类", "代码名称", "交易所/市场", "方向", "数量/面值", "价格", "成交金额", "总费用", "变动金额", "佣金", "平台费" };
            var rows = new string[][] { ["2025-01-02 10:00:00", "TEST", "证券", "DEMO", "US", "买入", "1", ".333", "-.33", ".01", "-.34", ".005", ".005"],
                ["2025-01-02 10:01:00", "TEST", "证券", "DEMO", "US", "买入", "1", ".333", "-.33", ".01", "-.34", ".005", ".005"],
                ["2025-01-03 10:00:00", "TEST", "证券", "DEMO", "US", "卖出", "-2", ".505", gross, ".03", ".98", ".01", ".02"] };
            // 列顺序允许变化；币种额外插入，验证不是依靠固定位置读取。
            Add(workbook, "证券-交易流水", columns.Concat(["币种"]).ToArray(), rows.Select(r => r.Concat(["USD"]).ToArray()).ToArray());
            if (missingPrice) workbook.GetSheetAt(1).GetRow(0).GetCell(7).SetCellValue("错误价格");
            if (missingFee) workbook.GetSheetAt(1).GetRow(0).GetCell(9).SetCellValue("错误总费用");
            Add(workbook, "证券-持仓总览", ["时期类型", "日期", "品类", "账户号码", "代码名称", "交易所/市场", "币种", "数量/面值", "价格"],
                [["期初", "20241231", "证券", "TEST", "DEMO", "US", "USD", openingQuantity, "99999"]]);
            Add(workbook, "证券-资产进出", ["日期", "账户号码", "代码名称", "交易所/市场", "币种", "数量", "方向", "类型", "备注"],
                assetEvent ? [["2025-01-01", "TEST", "DEMO", "US", "USD", "1", "In", "Gift Stock", "未知真实成本"]] : []);
            Add(workbook, "证券-资金进出", ["日期", "账户号码", "类型", "方向", "币种", "变动金额", "备注"],
                [["2025-01-05", "TEST", "公司行动(美股分红)", "In", "USD", "100", "DEMO 2 SHARES DIVIDENDS"],
                ["2025-01-05", "TEST", "公司行动", "Out", "USD", "-10", "Withholding Tax"],
                ["2025-01-06", "TEST", "公司行动", "In", "USD", refund, "Withholding Tax refund"],
                ["2025-01-01", "TEST", "出入金", "In", "USD", "200", "Deposit"]]);
            using var output = File.Create(tradePath); workbook.Write(output, true);
        }
        return [incomePath, tradePath];
    }
    /// <summary>写入测试表头与原始文本值，不执行任何公式。</summary>
    private static void Add(XSSFWorkbook workbook, string name, string[] fields, string[][] rows)
    {
        var sheet = workbook.CreateSheet(name); var header = sheet.CreateRow(0);
        for (var i = 0; i < fields.Length; i++) header.CreateCell(i).SetCellValue(fields[i]);
        for (var r = 0; r < rows.Length; r++) { var row = sheet.CreateRow(r + 1); for (var c = 0; c < rows[r].Length; c++) row.CreateCell(c).SetCellValue(rows[r][c]); }
    }
    /// <summary>生成只含英文收入表的可提取文字 PDF，避免测试依赖私有账户。</summary>
    private static string Pdf(string workspace)
    {
        var builder = new PdfDocumentBuilder(); var font = builder.AddStandard14Font(Standard14Font.Helvetica); var page = builder.AddPage(650, 800);
        page.AddText("2025 Annual Report", 12, new PdfPoint(10, 750), font);
        page.AddText("Account", 10, new PdfPoint(10, 650), font); page.AddText("Dividends", 10, new PdfPoint(160, 650), font);
        page.AddText("Interest", 10, new PdfPoint(300, 650), font); page.AddText("Other income", 10, new PdfPoint(440, 650), font);
        page.AddText("TEST", 10, new PdfPoint(10, 630), font); page.AddText("USD 100.00", 10, new PdfPoint(160, 630), font);
        page.AddText("USD 5.00", 10, new PdfPoint(300, 630), font); page.AddText("USD 0.00", 10, new PdfPoint(440, 630), font);
        var path = Path.Combine(workspace, "2025-test.pdf"); File.WriteAllBytes(path, builder.Build()); return path;
    }
    /// <summary>使用本机中文字体生成旧版中文税表测试布局，不把字体复制进仓库。</summary>
    private static string? ChinesePdf(string workspace)
    {
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "STFANGSO.TTF"); if (!File.Exists(fontPath)) return null;
        var builder = new PdfDocumentBuilder(); var font = builder.AddTrueTypeFont(File.ReadAllBytes(fontPath)); var page = builder.AddPage(800, 800);
        var fields = new[] { "牛牛号", "年份", "账户名称", "全年股息", "全年利息", "全年其他收入", "币种" };
        var values = new[] { "123456", "2025", "合成账户", "100", "5", "0", "USD" };
        for (var i = 0; i < fields.Length; i++) { page.AddText(fields[i], 10, new PdfPoint(20 + i * 100, 650), font); page.AddText(values[i], 10, new PdfPoint(20 + i * 100, 625), font); }
        var path = Path.Combine(workspace, "2025-chinese-test.pdf"); File.WriteAllBytes(path, builder.Build()); return path;
    }
    /// <summary>执行独立预期断言。</summary>
    private static void Assert(bool condition) { if (!condition) throw new Exception("富途断言不满足"); }
    /// <summary>无效源数据必须给出业务异常。</summary>
    private static void Reject(Action action) { try { action(); } catch (CrsException) { return; } throw new Exception("预期拒绝但被接受"); }
    /// <summary>真实模板入口验证使用独立固定汇率，不接触用户实际汇率配置。</summary>
    private sealed class VerificationRates : CRS.Application.Abstractions.IExchangeRateProvider
    {
        public string Fingerprint => "FUTU_TEMPLATE_VERIFICATION_ONLY";
        /// <summary>提供明确标记的测试值，仅用于验证流程可运行。</summary>
        public AppliedRate Get(int year, string currency) => new(year, currency, 1, "合成模板验证汇率", "https://example.com/verification", "固定测试值", new(year, 1, 1), new(year, 12, 31), false, true);
    }
}
