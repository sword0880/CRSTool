using System.Text.Json;

namespace CRS.Application;

/// <summary>把已独立完成的账户结果聚合到同一纳税人年度范围，不参与 FIFO 库存匹配。</summary>
public sealed class AnnualTaxAggregationService
{
    /// <summary>按纳税人范围合并多个账户结果，并向上传递最保守的完整性和对账状态。</summary>
    public CalculationResult Aggregate(string taxpayerScopeId, int year, IReadOnlyCollection<CalculationResult> results)
    {
        if (string.IsNullOrWhiteSpace(taxpayerScopeId)) throw new CrsException("纳税人范围标识不能为空。");
        if (results.Count == 0) throw new CrsException("至少需要一份账户计算结果才能进行年度聚合。");
        if (results.Any(r => r.Year != year)) throw new CrsException("年度聚合不能混入不同年度的计算结果。");
        if (results.Any(r => r.IsAnnualAggregate || r.Broker == "MULTI"))
            throw new CrsException("请选择原始账户任务，不能再次纳入已生成的年度汇总任务。");

        if (results.Any(r => r.CoveredAccounts.Count == 0 || r.CoveredAccounts.Any(string.IsNullOrWhiteSpace)))
            throw new CrsException("年度聚合需明确每份运行的账户范围。");
        if (results.Any(r => r.TaxpayerScopeId != taxpayerScopeId))
            throw new CrsException("年度聚合的账户运行必须属于同一纳税人范围。");
        var accountKeys = results.SelectMany(r => r.CoveredAccounts.Select(a => (Broker: r.Broker, Account: a))).ToList();
        var duplicate = accountKeys.GroupBy(k=>k).FirstOrDefault(g=>g.Count()>1);
        if (duplicate is not null)
            throw new CrsException($"年度聚合发现重复账户：{duplicate.Key.Broker} / {duplicate.Key.Account}，请选择该账户的一份任务。");
        var accounts = accountKeys.Select(k => k.Account).ToList();
        var rateGroups = results.SelectMany(r => r.Rates).GroupBy(r => (r.Year, r.Currency)).ToList();
        if (rateGroups.Any(g => g.Key.Year != year || string.IsNullOrWhiteSpace(g.Key.Currency) || g.Any(r => r.Value <= 0)))
            throw new CrsException("来源任务的冻结汇率存在错年度或无效币种、数值，不能聚合。");
        if (rateGroups.Any(g => g.Distinct().Count() != 1))
            throw new CrsException("同年度同币种的汇率数值或来源不一致，不能聚合。");
        var rates = rateGroups.Select(g => g.First()).OrderBy(r => r.Currency, StringComparer.Ordinal).ToList();
        var inputs = results.Where(r => r.TaxInputs is not null).Select(r => r.TaxInputs!).ToList();
        if (inputs.Any(i => i.Version != 1 || i.RuleVersion != TaxEngine.PolicyVersion || string.IsNullOrWhiteSpace(i.Broker)))
            throw new CrsException("年度事实或规则版本不兼容，不能聚合。");
        foreach (var run in results.Where(r => r.TaxInputs is not null))
        {
            var facts = run.TaxInputs!;
            if (facts.Broker != run.Broker || facts.Income.Any(i => i.Year != year || !run.CoveredAccounts.Contains(i.Account))
                || facts.ForeignTax.Any(t => t.Year != year || !run.CoveredAccounts.Contains(t.Account))
                || run.Matches.Any(m => m.SellDate.Year != year || !run.CoveredAccounts.Contains(m.Key.Account) || m.Key.Broker != facts.Broker))
                throw new CrsException("年度事实的账户、券商或年度超出运行范围。");
        }
        var income = inputs.SelectMany(i => i.Income).ToList();
        var foreignTax = inputs.SelectMany(i => i.ForeignTax).ToList();
        var matches = results.SelectMany(r => r.Matches).ToList();
        var withholding = foreignTax.GroupBy(t => t.Currency).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        // 汇总原币收入、净税款与已匹配收益后，只执行一次年度算法；不相加账户税额。
        var summary = TaxEngine.Calculate(income, matches, withholding, year, Rate);
        var issues = results.SelectMany(r => r.Issues).Distinct().ToList();
        // 国家分配必须覆盖合并后的整个年度，不能简单相加各账户已限额的抵免金额。
        if (foreignTax.Any(t => t.Amount != 0)) issues.Add(new("TAX_CREDIT_UNVERIFIED",
            "年度汇总需重新分配全年度所得来源国家并关联税款凭证，请导入覆盖全部来源账户的抵免明细。"));
        var factsMissing = inputs.Count != results.Count;
        if (factsMissing)
            issues.Add(new("TAX_FACTS_MISSING", "历史运行缺少可聚合收入或未限额税款事实，仅显示已有事实，年度预计补税保持为空。"));
        if (results.Any(r => r.CalculationStatus != CalculationStatus.Completed))
            issues.Add(new("AGGREGATION_INCOMPLETE", "纳入年度范围的账户结果存在未完成项，年度预计补税保持为空。"));
        issues = issues.Distinct().ToList();
        var status = results.Any(r => r.CalculationStatus == CalculationStatus.Blocked) ? CalculationStatus.Blocked
            : issues.Count == 0 && results.All(r => r.CalculationStatus == CalculationStatus.Completed) ? CalculationStatus.Completed : CalculationStatus.Partial;
        var completeness = factsMissing || results.Any(r => r.DataCompleteness == DataCompleteness.Incomplete) ? DataCompleteness.Incomplete
            : results.All(r => r.DataCompleteness == DataCompleteness.Confirmed) ? DataCompleteness.Confirmed : DataCompleteness.Unconfirmed;
        var reconciliation = results.Any(r => r.ReconciliationStatus == ReconciliationStatus.Differences) ? ReconciliationStatus.Differences
            : results.All(r => r.ReconciliationStatus == ReconciliationStatus.Matched) ? ReconciliationStatus.Matched : ReconciliationStatus.NotVerifiable;
        var provisional = results.Any(r => r.Provisional || r.UsageLabel == UsageLabel.Provisional);
        var usage = provisional ? UsageLabel.Provisional : status == CalculationStatus.Completed && reconciliation == ReconciliationStatus.Matched
            ? UsageLabel.ReviewReady : UsageLabel.ReviewOnly;
        var estimated = !factsMissing && status == CalculationStatus.Completed && completeness == DataCompleteness.Confirmed
            && reconciliation == ReconciliationStatus.Matched && !provisional
            && results.All(r => r.EstimatedTopUpCny is not null && r.Complete) ? (decimal?)summary.SupplementTax : null;
        summary = summary with { EstimatedTopUpCny = estimated };
        var snapshotId = Guid.NewGuid().ToString("N");
        var sourceIds = results.Select(r => r.SnapshotId).Where(id => id != "").Order(StringComparer.Ordinal).ToArray();
        var result = new CalculationResult
        {
            Broker = results.Select(r => r.Broker).Distinct().Count() == 1 ? results.First().Broker : "MULTI",
            AggregationSources = results.OrderBy(r => r.SnapshotId, StringComparer.Ordinal)
                .Select(r => new AggregationSource(r.SnapshotId, r.Broker, r.Year, r.CoveredAccounts.Order(StringComparer.Ordinal).ToArray())).ToList(),
            ImportedTradeCount = results.All(r => r.ImportedTradeCount is not null) ? results.Sum(r => r.ImportedTradeCount!.Value) : null,
            AnnualIncome = results.SelectMany(r => r.AnnualIncome).ToList(), FundMovements = results.SelectMany(r => r.FundMovements).ToList(),
            TradeAmountChecks = results.SelectMany(r => r.TradeAmountChecks).ToList(), SecurityMovements = results.SelectMany(r => r.SecurityMovements).ToList(),
            Year = year,
            Summary = summary,
            // 单券商事实可继续供后续聚合使用；混合券商需未来的逐账户契约，不隐式丢失身份。
            TaxInputs = !factsMissing && inputs.Select(i => i.Broker).Distinct().Count() == 1
                ? new TaxCalculationInputs(1, TaxEngine.PolicyVersion, income, foreignTax, inputs[0].Broker) : null,
            Matches = matches,
            Issues = issues,
            Warnings = results.SelectMany(r => r.Warnings).Distinct().ToList(),
            CashChecks = results.SelectMany(r => r.CashChecks).ToList(),
            PnlChecks = results.SelectMany(r => r.PnlChecks).ToList(),
            Rates = rates,
            Sources = results.SelectMany(r => r.Sources).ToList(),
            EndingLots = [], EndingRounding = [], OpeningZero = results.All(r => r.OpeningZero), ScopeConfirmed = results.All(r => r.ScopeConfirmed),
            Provisional = provisional, Reconciled = reconciliation == ReconciliationStatus.Matched,
            CarryEligible = false, CalculationStatus = status, DataCompleteness = completeness, ReconciliationStatus = reconciliation,
            UsageLabel = usage, TaxpayerScopeId = taxpayerScopeId, CoveredAccounts = accounts.Distinct().Order().ToList(),
            UnknownGainCount = results.Sum(r => r.UnknownGainCount), EstimatedTopUpCny = estimated,
            InputRecoveryMode = InputRecoveryMode.Unavailable, IsReplayable = false, SnapshotId = snapshotId
        };
        // 聚合快照只记录父运行编号和状态，账户原始快照仍保持不可变并可单独追溯。
        result.SnapshotJson = JsonSerializer.Serialize(new
        {
            result.SnapshotId, result.TaxpayerScopeId, result.Year, parentSnapshotIds = sourceIds,
            aggregationSources = result.AggregationSources,
            result.CalculationStatus, result.DataCompleteness, result.ReconciliationStatus, result.UsageLabel,
            createdUtc = DateTimeOffset.UtcNow
        });
        return result;

        /// <summary>只采用父运行的冻结汇率，缺失时拒绝聚合，不引入当前配置。</summary>
        decimal Rate(int taxYear, string currency)
        {
            var rate = rates.SingleOrDefault(r => r.Year == taxYear && r.Currency == currency)
                ?? throw new CrsException($"年度聚合缺少 {taxYear} 年 {currency} 冻结汇率。");
            if (rate.Value <= 0) throw new CrsException("年度聚合汇率必须为正数。");
            return rate.Value;
        }
    }
}
