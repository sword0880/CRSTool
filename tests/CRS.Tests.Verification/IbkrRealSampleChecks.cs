using System.Security.Cryptography;
using System.Text.Json;
using CRS.Infrastructure;

/// <summary>真实 IBKR 原件的技术往返验证；只输出期间、笔数和问题代码，不输出身份与金额。</summary>
internal static class IbkrRealSampleChecks
{
    public static void Run(string directory, string config, string output, string workspace)
    {
        var previous = Path.Combine(directory, "IBKR_2025.xml");
        var current = Path.Combine(directory, "IBKR_2026.xml");
        var singleDay = Path.Combine(directory, "flex.1664545.2025TotalYear.20261009.20261009.xml");
        string[] originals = [previous, current, singleDay];
        var before = originals.Select(Hash).ToArray();
        var store = TestStores.Create(Path.Combine(workspace, "ibkr-real-encrypted-store"));
        var rows = new List<object>();
        // 年份以报告正文核对；开户前缺口不擅自视为零持仓，单日资料也不视为完整年度。
        foreach (var sample in new[] {
            (Year: 2025, Kind: "mid_year_account", Files: new[] { previous }),
            (Year: 2026, Kind: "year_to_date", Files: new[] { current }),
            (Year: 2026, Kind: "year_to_date_with_previous_history", Files: new[] { previous, current }),
            (Year: 2026, Kind: "single_day", Files: new[] { singleDay }),
            (Year: 2026, Kind: "single_day_with_previous_history", Files: new[] { previous, singleDay }) })
        {
            try
            {
                var parsed = new IbkrXmlParser().Parse(sample.Files);
                var periods = parsed.Sources.Where(s => s.Start.Year == sample.Year && s.End.Year == sample.Year)
                    .Select(s => new { start = s.Start, end = s.End }).Distinct().ToArray();
                if (periods.Length == 0) throw new Exception("报告正文年度不符合用户提供的年度。");
                var calculator = CalculationServices.Create(new ExchangeRates(config), "IBKR", sample.Year);
                var result = calculator.Calculate(sample.Files, sample.Year, false, false, saveCanonicalSnapshot: true);
                store.Save(result);
                var loaded = store.Load(result.SnapshotId);
                var replay = calculator.Replay(loaded); store.Save(replay);
                if (result.Summary != replay.Summary || !result.Matches.SequenceEqual(replay.Matches)
                    || !Rows(result.PnlChecks).SequenceEqual(Rows(replay.PnlChecks))
                    || !Rows(result.CashChecks).SequenceEqual(Rows(replay.CashChecks))
                    || !loaded.IsFrozen || result.EstimatedTopUpCny is not null || result.Complete
                    || !result.Issues.Any(i => i.Code == "INCOMPLETE_PERIOD")
                    || sample.Year == 2026 && !result.Provisional)
                    throw new Exception("技术往返或期间门槛不一致。");
                // 以券商原始收益对账验证历史成本链；缺失历史时必须提示，不能静默补零成本。
                if (sample.Kind == "year_to_date" && !result.Issues.Any(i => i.Code == "MISSING_COST"))
                    throw new Exception("缺少跨年成本应进入复核。");
                if (sample.Kind == "year_to_date_with_previous_history" &&
                    (result.Issues.Any(i => i.Code == "MISSING_COST") || result.PnlChecks.Count == 0
                     || result.PnlChecks.Any(p => p.Status != "一致")))
                    throw new Exception("历史补齐后券商收益核对未闭合。");
                rows.Add(new { year = sample.Year, kind = sample.Kind, periods,
                    // IBKR 收入事实来自现金记录，AnnualIncome 是富途主表，不能用它表示 IBKR 收入是否存在。
                    trades = result.ImportedTradeCount, matches = result.Matches.Count, incomeRows = result.TaxInputs?.Income.Count ?? 0,
                    cashRows = parsed.Cash.Count(c => c.Date.Year == sample.Year), provisional = result.Provisional,
                    pnlChecks = result.PnlChecks.Count, pnlMatched = result.PnlChecks.Count(p => p.Status == "一致"),
                    cashChecks = result.CashChecks.Count, cashMatched = result.CashChecks.Count(c => c.Status == "一致"),
                    issueCodes = result.Issues.Select(i => i.Code).Distinct().Order().ToArray(),
                    status = result.CalculationStatus.ToString(), persistenceAndReplayVerified = true });
            }
            catch (Exception error)
            {
                // 解析异常可能携带账户或原始行内容，真实样本入口仅记录异常类型，禁止透传敏感消息。
                Console.Error.WriteLine($"IBKR {sample.Year} 技术验收失败：{error.GetType().Name}。");
                Environment.ExitCode = 1; return;
            }
        }
        if (!before.SequenceEqual(originals.Select(Hash))) throw new Exception("验收期间 IBKR 原件发生变化。");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "ibkr-real-samples.json"), JsonSerializer.Serialize(new {
            sourceKind = "real-private", broker = "IBKR", originalsUnchanged = true,
            originals = before.Select((sha256, index) => new { index, sha256 }), rows,
            independentAmountsApproved = false, releaseApproved = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("IBKR 五种真实资料组合导入、SQLCipher 保存和冻结复算一致；原件未改写，年度完整性与独立金额签认仍待复核。");
    }
    // 只用摘要证明原件保持不变，不在验收产物记录文件名或原始数据。
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    // 完整规范化快照按稳定顺序恢复记录，核对完整内容而不把展示顺序误判为业务差异。
    private static string[] Rows<T>(IEnumerable<T> rows) => rows.Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal).ToArray();
}
