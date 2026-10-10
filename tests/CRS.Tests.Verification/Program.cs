using System.Text.Json;
using System.Xml.Linq;
using CRS.Domain;
using CRS.Infrastructure;

// 使用独立控制台验证金额与状态，不需要启动桌面窗口。
var workspace = Path.Combine(Path.GetTempPath(), "CRS-Verification-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
var config = Path.Combine(workspace, "config"); Directory.CreateDirectory(config);
File.WriteAllText(Path.Combine(config, "exchange_rate.json"), """{"2025":{"USD":1,"HKD":1,"CNY":1},"2026":{"USD":1,"HKD":1,"CNY":1}}""");
File.WriteAllText(Path.Combine(config, "exchange_rate_sources.json"), """{"2025":{"source":"合成验证数据","url":"https://example.com/verification","method":"固定测试汇率","start_date":"2025-01-01","end_date":"2025-12-31","provisional":false,"rates":{"USD":1,"HKD":1}}}""");
var rates = new ExchangeRates(config);
// 性能入口与普通回归分开，避免把合成大样本耗时混入测试正确性结论。
if (args.Length == 2 && args[0] == "--benchmark") { ReleaseBenchmark.Run(args[1],workspace,rates); return; }
// 真实样本只能来自用户提供的脱敏原件和独立人工基准，不能从合成 fixture 自动升级。
if (args.Length == 3 && args[0] == "--accept-real") { RealSampleAcceptance.Run(args[1],args[2]); return; }
if (args.Length >= 2 && args[0] == "--analyze")
{
    var analyzed = CalculationServices.Create(args.Length >= 3 ? new ExchangeRates(args[2]) : rates).Calculate([args[1]], 2025, true, true);
    Console.WriteLine(JsonSerializer.Serialize(new { matches = analyzed.Matches.Count, pnl = analyzed.PnlChecks.Count,
        pnlConsistent = analyzed.PnlChecks.Count(p => p.Status == "一致"), cashConsistent = analyzed.CashChecks.Count(c => c.Status == "一致"),
        gain = analyzed.Matches.Sum(m => m.Gain), cost = analyzed.Matches.Sum(m => m.Cost), revenue = analyzed.Matches.Sum(m => m.Revenue),
        buyFee = analyzed.Matches.Sum(m => m.BuyFee), sellFee = analyzed.Matches.Sum(m => m.SellFee), summary = analyzed.Summary,
        lots = analyzed.EndingLots.Select(l => new { l.Key.Instrument, l.Key.Currency, l.Quantity, l.Cost, l.Fee }) }));
    return;
}
int passed = 0;
// 执行独立场景并汇总失败，单个断言失败不妨碍其他场景检查。
void Check(string name, Action test) { try { test(); passed++; Console.WriteLine("通过：" + name); } catch (Exception e) { Console.Error.WriteLine("失败：" + name + "；" + e.Message); if (args.Contains("--debug")) Console.Error.WriteLine(e.StackTrace); Environment.ExitCode = 1; } }
// 要求金额或状态满足预期，不满足时标记当前验证失败。
void Assert(bool condition) { if (!condition) throw new Exception("断言不满足"); }
// 检查无效资料明确产生业务异常，而非被静默接受。
void Reject(Action action) { try { action(); } catch (CrsException) { return; } throw new Exception("预期拒绝但被接受"); }
var key = new SecurityKey("TEST", "111", "USD");
// 合成报告每次创建成交都具有独立原始行号，明确表达原有测试已知的成交先后。
var syntheticRow = 0;
// 创建合成成交，保留指定总金额，以验证分配而非重新计算价格金额。
Trade Trade(string id, string side, decimal qty, decimal gross, decimal fee = 0, int year = 2025, int day = 2) => new(key, "DEMO", new(year, 1, day, 10, 0, 0, TimeSpan.Zero), false,
    side, qty, gross / qty, gross, fee, side == "BUY" ? -gross - fee : gross - fee, id, "synthetic.xml", ++syntheticRow, "STK", null);
// 使用固定测试汇率运行 FIFO，独立于用户汇率文件。
List<Match> Run(Trade[] trades, FifoEngine? engine = null) => (engine ?? new()).Calculate(trades, [], [], [], 2025, (_, _) => 1);
// 创建覆盖全年、可控制盈亏及余额的脱敏合成 XML。
string Xml(decimal pnl = 496, decimal ending = 496, bool cashReport = true, string asset = "STK")
{
    var root = XElement.Parse("""<FlexQueryResponse type="AF"><FlexStatements><FlexStatement accountId="TEST" fromDate="20250101" toDate="20251231"><Trades/><CashTransactions/><OpenPositions/></FlexStatement></FlexStatements></FlexQueryResponse>""");
    var s = root.Descendants("FlexStatement").Single();
    foreach (var t in new[] { Trade("B", "BUY", 10, 1000, 2), Trade("S", "SELL", 10, 1500, 2, day: 3) })
        s.Element("Trades")!.Add(new XElement("Trade", new XAttribute("accountId", "TEST"), new XAttribute("assetCategory", asset), new XAttribute("conid", "111"), new XAttribute("symbol", "DEMO"), new XAttribute("currency", "USD"),
            new XAttribute("tradeID", t.Id), new XAttribute("levelOfDetail", "EXECUTION"), new XAttribute("dateTime", t.Time.ToString("yyyyMMdd;HHmmss")), new XAttribute("tradeDate", t.Time.ToString("yyyyMMdd")),
            new XAttribute("buySell", t.Side), new XAttribute("quantity", t.Side == "BUY" ? t.Quantity : -t.Quantity), new XAttribute("tradePrice", t.Price), new XAttribute("ibCommission", -t.Fee),
            new XAttribute("ibCommissionCurrency", "USD"), new XAttribute("taxes", 0), new XAttribute("multiplier", 1), new XAttribute("proceeds", t.Side == "BUY" ? -t.Gross : t.Gross), new XAttribute("netCash", t.Net),
            new XAttribute("openCloseIndicator", t.Side == "BUY" ? "O" : "C"), new XAttribute("transactionType", "ExchTrade"), new XAttribute("fifoPnlRealized", t.Side == "SELL" ? pnl : 0)));
    if (cashReport) s.Add(new XElement("CashReport", new XElement("CashReportCurrency", new XAttribute("currency", "USD"), new XAttribute("levelOfDetail", "Currency"), new XAttribute("fromDate", "20250101"), new XAttribute("toDate", "20251231"), new XAttribute("startingCash", 0), new XAttribute("endingCash", ending))));
    var path = Path.Combine(workspace, Guid.NewGuid() + ".xml"); root.Save(path); return path;
}
var service = CalculationServices.Create(rates);
/// <summary>构造独立账户事实及可信运行，用于验证聚合算法而非相加已算税额。</summary>
CalculationResult AccountRun(string account, decimal gain, decimal dividend = 0, decimal paid = 0)
{
    var matches = Run([Trade("B", "BUY", 1, 1000), Trade("S", "SELL", 1, 1000 + gain, day: 3)])
        .Select(m => m with { Key = m.Key with { Account = account } }).ToList();
    var income = new List<Income> { new(account, 2025, "USD", dividend, 0) };
    var summary = TaxEngine.Calculate(income, matches, new Dictionary<string, decimal> { ["USD"] = paid }, 2025, (_, _) => 1);
    return new CalculationResult { Year = 2025, Summary = summary with { EstimatedTopUpCny = summary.SupplementTax },
        Matches = matches, TaxInputs = new(1, TaxEngine.PolicyVersion, income, [new(account, 2025, "USD", paid)]),
        Rates = [rates.Get(2025, "USD")], CoveredAccounts = [account], SnapshotId = Guid.NewGuid().ToString("N"),
        CalculationStatus = CalculationStatus.Completed, DataCompleteness = DataCompleteness.Confirmed,
        ReconciliationStatus = ReconciliationStatus.Matched, UsageLabel = UsageLabel.ReviewReady, Reconciled = true,
        ScopeConfirmed = true, EstimatedTopUpCny = summary.SupplementTax };
}
Check("AR-01-01 年度盈利亏损先汇总再计算税额", () => {
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [AccountRun("A", 100), AccountRun("B", -100)]);
    Assert(r.Summary.GainCny == 0 && r.Summary.GainTax == 0 && r.EstimatedTopUpCny == 0 && r.Complete);
});
Check("AR-01-02 保留未限额税款统一计算抵免", () => {
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [AccountRun("A", 0, 100, 100), AccountRun("B", 0, 100)]);
    Assert(r.Summary.DividendInterestTax == 40 && r.Summary.ForeignCredit == 40 && r.Summary.SupplementTax == 0);
});
Check("AR-01-03 年度税额统一舍入", () => {
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [AccountRun("A", 0, .025m), AccountRun("B", 0, .025m)]);
    Assert(r.Summary.DividendCny == .06m && r.Summary.DividendInterestTax == .01m);
});
Check("AR-01-04 拒绝重复账户与空范围", () => {
    Reject(() => new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [AccountRun("A", 1), AccountRun("A", 2)]));
    Reject(() => new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [new CalculationResult { Year = 2025, Summary = new(0,0,0,0,0,0,0) }]));
});
Check("AR-01-05 拒绝冲突汇率及错年度事实", () => {
    var a = AccountRun("A", 1); var b = AccountRun("B", 1); b.Rates[0] = b.Rates[0] with { Value = 2 };
    Reject(() => new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [a,b]));
    var wrong = AccountRun("C", 0); wrong.TaxInputs!.Income[0] = wrong.TaxInputs.Income[0] with { Year = 2024 };
    Reject(() => new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [wrong]));
});
Check("AR-01-06 原币事实保存加载后仍可聚合", () => {
    var db = TestStores.Create(Path.Combine(workspace, "aggregation-db")); var a = AccountRun("A", 100); var b = AccountRun("B", -100);
    db.Save(a); db.Save(b);
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [db.Load(a.SnapshotId), db.Load(b.SnapshotId)]);
    Assert(r.EstimatedTopUpCny == 0 && r.TaxInputs is not null);
});
Check("AR-01-07 旧运行缺少事实不得反推完整税额", () => {
    var legacy = new CalculationResult { Year = 2025, Summary = new(100,0,0,20,0,20,0), CoveredAccounts = ["OLD"],
        CalculationStatus = CalculationStatus.Completed, DataCompleteness = DataCompleteness.Confirmed,
        ReconciliationStatus = ReconciliationStatus.Matched, Reconciled = true, EstimatedTopUpCny = 0 };
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [legacy]);
    Assert(r.EstimatedTopUpCny is null && r.DataCompleteness == DataCompleteness.Incomplete && r.Issues.Any(i => i.Code == "TAX_FACTS_MISSING"));
});
Check("AR-01-08 税款退回按年度净事实抵免", () => {
    var r = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025,
        [AccountRun("A", 0, 100, 10), AccountRun("B", 0, 100, -4)]);
    Assert(r.Summary.ForeignCredit == 6 && r.Summary.SupplementTax == 34);
});
Check("AR-01-09 缺冻结汇率或混用纳税人范围拒绝", () => {
    var missing = AccountRun("A", 0, 100); missing.Rates.Clear();
    Reject(() => new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [missing]));
    Reject(() => new AnnualTaxAggregationService().Aggregate("ANOTHER_USER", 2025, [AccountRun("B", 0)]));
});
Check("单笔卖出匹配多个小额批次守恒", () => { var m = Run([Trade("B1", "BUY", 1, .01m), Trade("B2", "BUY", 1, .01m), Trade("B3", "BUY", 1, .01m), Trade("S", "SELL", 3, .10m, .01m, day: 3)]); Assert(m.Count == 3 && m.Sum(v => v.Revenue) == .10m && m.Sum(v => v.SellFee) == .01m && m.Sum(v => v.Gain) == .06m); });
Check("COST-001 含费总成本只扣一次", () => { var lot = new CostLot { Key = key, Symbol = "DEMO", BuyTime = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero), Quantity = 1, Cost = 1002, Fee = 2, CostBasisMode = CostBasisMode.TotalCostIncludesFees, RecordId = "OPEN", SourceFile = "opening.xml" }; var m = new FifoEngine().Calculate([Trade("S", "SELL", 1, 1200, 3)], [lot], [], [], 2025, (_, _) => 1); Assert(m.Single().Cost == 1002 && m.Single().BuyFee == 2 && m.Single().Gain == 195); });
Check("一次买入跨多次卖出成本守恒", () => { var m = Run([Trade("B", "BUY", 3, 3.01m, .01m), Trade("S1", "SELL", 1, 2, day: 3), Trade("S2", "SELL", 1, 2, day: 4), Trade("S3", "SELL", 1, 2, day: 5)]); Assert(m.Sum(v => v.Cost) == 3.02m && m.Sum(v => v.BuyFee) == .01m); });
Check("账户库存隔离", () => { var e = new FifoEngine(); Assert(Run([Trade("B", "BUY", 1, 1), Trade("S", "SELL", 1, 2, day: 3) with { Key = key with { Account = "OTHER" } }], e).Count == 0 && e.Issues.Count == 1); });
Check("缺失成本不能被后来买入恢复", () => { var e = new FifoEngine(); Assert(Run([Trade("S1", "SELL", 1, 2), Trade("B", "BUY", 1, 1, day: 3), Trade("S2", "SELL", 1, 2, day: 4)], e).Count == 0 && e.Issues.Count == 2); });
Check("完整合成报告对账", () => { var r = service.Calculate([Xml()], 2025, true, true); Assert(r.Complete && r.Summary.GainCny == 496 && r.Summary.SupplementTax == 99.2m && r.EstimatedTopUpCny == 99.2m && r.CalculationStatus == CalculationStatus.Completed && r.DataCompleteness == DataCompleteness.Confirmed && r.ReconciliationStatus == ReconciliationStatus.Matched && !r.IsReplayable && r.InputRecoveryMode == InputRecoveryMode.ReimportOriginalFiles && r.CanonicalInputDigest != "" && r.CarryEligible); });
Check("REPLAY-001 原始文件摘要校验", () => { var p = Xml(); var r = service.Calculate([p], 2025, true, true); service.VerifyOriginalFiles(r, [p]); File.AppendAllText(p, " "); Reject(() => service.VerifyOriginalFiles(r, [p])); });
Check("REPLAY-002 规范化快照可重放", () => { var p = Xml(); var r = service.Calculate([p], 2025, true, true, saveCanonicalSnapshot: true); var replay = service.Replay(r); Assert(r.IsReplayable && replay.IsReplayable && replay.InputRecoveryMode == InputRecoveryMode.CanonicalLocalSnapshot && replay.Summary == r.Summary && replay.Matches.Count == r.Matches.Count && replay.CanonicalInputDigest == r.CanonicalInputDigest); });
Check("AGG-001 账户不完整状态向年度聚合继承", () => { var complete = service.Calculate([Xml()], 2025, true, true); var partial = new CalculationResult { Year = 2025, Summary = new(0, 0, 0, 0, 0, 0, 0), CalculationStatus = CalculationStatus.Partial, DataCompleteness = DataCompleteness.Incomplete, ReconciliationStatus = ReconciliationStatus.Differences, UsageLabel = UsageLabel.ReviewOnly, CoveredAccounts = ["SECOND"], Issues = [new("MISSING_COST", "账户成本缺失")] }; var aggregate = new AnnualTaxAggregationService().Aggregate("LOCAL_USER", 2025, [complete, partial]); Assert(aggregate.CalculationStatus == CalculationStatus.Partial && aggregate.DataCompleteness == DataCompleteness.Incomplete && aggregate.ReconciliationStatus == ReconciliationStatus.Differences && aggregate.EstimatedTopUpCny is null && aggregate.Issues.Any(i => i.Code == "AGGREGATION_INCOMPLETE")); });
Check("重复报告不会重复收益", () => { var p = Xml(); var r = service.Calculate([p, p], 2025, true, true); Assert(r.Summary.GainCny == 496 && r.Matches.Count == 1); });
Check("同 ID 内容冲突拒绝", () => { Reject(() => service.Calculate([Xml(), Xml(pnl: 500)], 2025, true, true)); });
Check("现金差异必须待复核", () => { var r = service.Calculate([Xml(ending: 500)], 2025, true, true); Assert(!r.Complete && r.EstimatedTopUpCny is null && r.CalculationStatus == CalculationStatus.Partial && r.CashChecks[0].Status == "存在差异"); });
Check("缺少余额不是零余额", () => { var r = service.Calculate([Xml(cashReport: false)], 2025, true, true); Assert(!r.Complete && r.CashChecks[0].Starting is null); });
Check("券商收益差异必须待复核", () => { Assert(!service.Calculate([Xml(pnl: 500)], 2025, true, true).Complete); });
Check("IBKR 原始成交金额异常需待复核", () => { var p = Xml(); var document = XDocument.Load(p); document.Descendants("Trade").Single(e => e.Attribute("tradeID")?.Value == "S").SetAttributeValue("proceeds", "1499"); document.Save(p); var r = service.Calculate([p], 2025, true, true); Assert(r.Issues.Any(i => i.Code == "BROKER_AMOUNT_MISMATCH") && !r.Complete); });
Check("导出范围必须确认", () => { Assert(!service.Calculate([Xml()], 2025, true, false).Complete); });
Check("期初必须确认", () => { Assert(!service.Calculate([Xml()], 2025, false, true).Complete); });
Check("未支持资产不能计入股票收益", () => { var r = service.Calculate([Xml(asset: "OPT")], 2025, true, true); Assert(!r.Complete && r.Matches.Count == 0); });
Check("DTD 被拒绝", () => { var p = Path.Combine(workspace, "dtd.xml"); File.WriteAllText(p, "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///no-file'>]><x>&a;</x>"); Reject(() => new IbkrXmlParser().Parse([p])); });
Check("无时分秒成交被拒绝", () => { var p = Xml(); var d = XDocument.Load(p); d.Descendants("Trade").First().SetAttributeValue("dateTime", "20250102"); d.Save(p); Reject(() => service.Calculate([p], 2025, true, true)); });
Check("SQLite 计算及复核记录持久化", () => { var db = TestStores.Create(Path.Combine(workspace, "db")); var r = service.Calculate([Xml()], 2025, true, true); db.Save(r); db.AddReview(r.SnapshotId, "普通换汇", "已查原始成交记录"); Assert(db.Load(r.SnapshotId).Summary == r.Summary && db.History().Count == 1 && db.Reviews(r.SnapshotId).Count == 1); });
Check("NPOI 报告写入与读取", () => { var r = service.Calculate([Xml()], 2025, true, true); var p = Path.Combine(workspace, "report.xlsx"); ExcelReports.Export(p, r); var rows = ExcelReports.ReadSheet(p, "税务汇总"); Assert(rows.Count == 9 && rows[3][1] == "496.00"); });
Check("结转摘要及年度验证", () => { var r = service.Calculate([Xml()], 2025, true, true); var p = Path.Combine(workspace, "carry.json"); File.WriteAllBytes(p, CarryFiles.Encode(CarryFiles.Create(r))); var d = CarryFiles.Read(p); CarryFiles.Validate(d, 2026, ["TEST"]); Reject(() => CarryFiles.Validate(d, 2025, ["TEST"])); });
Check("结转不接受篡改", () => { var r = service.Calculate([Xml()], 2025, true, true); var p = Path.Combine(workspace, "tampered.json"); File.WriteAllText(p, System.Text.Encoding.UTF8.GetString(CarryFiles.Encode(CarryFiles.Create(r))).Replace("2025", "2024")); Reject(() => CarryFiles.Read(p)); });
Check("全年亏损不计资本收益税", () => { var m = Run([Trade("B", "BUY", 1, 10), Trade("S", "SELL", 1, 5, day: 3)]); Assert(TaxEngine.Calculate([], m, new Dictionary<string, decimal>(), 2025, (_, _) => 1).GainTax == 0); });
Check("扣税退回按净额抵免", () => { var s = TaxEngine.Calculate([new("TEST", 2025, "USD", 100, 20)], [], new Dictionary<string, decimal> { ["USD"] = 6 }, 2025, (_, _) => 1); Assert(s.ForeignCredit == 6 && s.SupplementTax == 18); });
Check("跨年结转保留小数成本余差", () => {
    var engine = new FifoEngine(); engine.Calculate([Trade("B", "BUY", 3, 3.01m, .01m), Trade("S1", "SELL", 1, 2, day: 3)], [], [], [], 2025, (_, _) => 1);
    var before = engine.EndingLots.Select(l => l.Copy()).ToList(); var remainder = engine.EndingRounding.ToList();
    var firstCost = 1.01m; var firstFee = 0m;
    var next = new FifoEngine().Calculate([Trade("S2", "SELL", 2, 4, year: 2026)], before, remainder, [], 2026, (_, _) => 1);
    Assert(firstCost + next.Sum(m => m.Cost) == 3.02m && firstFee + next.Sum(m => m.BuyFee) == .01m);
});
Check("公司行动阻断成本", () => { var e = new FifoEngine(); var m = e.Calculate([Trade("B", "BUY", 1, 1), Trade("S", "SELL", 1, 2, day: 3)], [], [], [new("ACTION", "未支持公司行动", "TEST", Instrument: "111", AffectsCost: true)], 2025, (_, _) => 1); Assert(m.Count == 0 && e.Uncertain.Contains(key)); });
Check("时区混用被拒绝", () => { Reject(() => Run([Trade("B", "BUY", 1, 1), Trade("S", "SELL", 1, 2, day: 3) with { HasOffset = true }])); });
Check("年度筛选不混入未来卖出", () => { Assert(Run([Trade("B", "BUY", 1, 1), Trade("S", "SELL", 1, 2, year: 2026)]).Count == 0); });
Check("币种库存隔离", () => { Assert(Run([Trade("B", "BUY", 1, 1), Trade("S", "SELL", 1, 2, day: 3) with { Key = key with { Currency = "HKD" } }]).Count == 0); });
Check("汇率正数校验", () => { var directory = Path.Combine(workspace, "bad-rate"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "exchange_rate.json"), """{"2025":{"USD":0}}"""); Reject(() => new ExchangeRates(directory)); });
Check("汇率重复键拒绝", () => { var directory = Path.Combine(workspace, "duplicate-rate"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "exchange_rate.json"), """{"2025":{"USD":1,"USD":2}}"""); Reject(() => new ExchangeRates(directory)); });
Check("换汇现金双币种核对", () => { var p = Xml(); var root = XDocument.Load(p); var s = root.Descendants("FlexStatement").Single(); var fx = new XElement(s.Element("Trades")!.Elements().First());
    foreach (var pair in new Dictionary<string, string> { ["tradeID"] = "FX", ["assetCategory"] = "CASH", ["symbol"] = "USD.HKD", ["currency"] = "HKD", ["quantity"] = "100", ["tradePrice"] = "7.8", ["ibCommission"] = "-2", ["proceeds"] = "-780", ["netCash"] = "-780" }) fx.SetAttributeValue(pair.Key, pair.Value);
    s.Element("Trades")!.Add(fx); var cash = s.Element("CashReport")!.Elements().Single(); cash.SetAttributeValue("endingCash", 594); var hkd = new XElement(cash); hkd.SetAttributeValue("currency", "HKD"); hkd.SetAttributeValue("startingCash", 1000); hkd.SetAttributeValue("endingCash", 220); s.Element("CashReport")!.Add(hkd); root.Save(p);
    var r = service.Calculate([p], 2025, true, true); Assert(r.CashChecks.Count == 2 && r.CashChecks.All(c => c.Status == "一致") && !r.Complete);
});
Check("Flex 协议及闰年查询", () => { using var handler = new FakeFlexHandler(File.ReadAllText(Xml())); using var http = new HttpClient(handler);
    var bytes = new FlexClient(http).DownloadAsync("123", "test-only-token", new(2024, 1, 1), new(2024, 12, 31), CancellationToken.None).GetAwaiter().GetResult(); Assert(bytes.Length > 0 && handler.Calls == 2); });
Check("Flex 取消请求", () => { using var handler = new FakeFlexHandler("<ignored/>"); using var http = new HttpClient(handler); using var cts = new CancellationTokenSource(); cts.Cancel();
    try { new FlexClient(http).DownloadAsync("123", "test-only-token", new(2025, 1, 1), new(2025, 12, 31), cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { return; } throw new Exception("取消未生效"); });
FutuVerification.Run(Check, workspace, rates);
if (args.Length >= 2 && args[0] == "--futu-samples") FutuVerification.VerifySamples(Check, args[1], args.Length >= 3 && args[2] != "--debug" ? args[2] : null);
Console.WriteLine($"验证完成：{passed} 项通过。临时文件：{workspace}");

sealed class FakeFlexHandler(string report) : HttpMessageHandler
{
    public int Calls { get; private set; }
    /// <summary>模拟 Flex 回执与下载，测试不访问实际 IBKR 或真实服务令牌。</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        if (request.RequestUri?.Host != "ndcdyn.interactivebrokers.com") throw new Exception("非固定官方域名");
        var text = Calls == 1 ? "<FlexStatementResponse><Status>Success</Status><ReferenceCode>REF1</ReferenceCode><Url>https://invalid.example/</Url></FlexStatementResponse>" : report;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(text) });
    }
}
