using System.Text.Json;
using System.Text.Json.Nodes;

namespace CRS.Application;

/// <summary>所得归属明细；只支持已导入的股息和利息，不按币种猜测来源国家。</summary>
public sealed record CreditIncome(string Broker, string Account, string Country, string Category, string Currency, decimal Amount);
/// <summary>凭证仅冻结名称和实际文件摘要，不保存正文或本地绝对路径。</summary>
public sealed record CreditProof(string Role, string Name, string Sha256);
/// <summary>税款与所得项目、账户和缴税凭证的一对一关系；排除原因非空时不参与抵免。</summary>
public sealed record CreditPayment(string Broker, string Account, string Country, string Category, string Currency,
    decimal Amount, string Authority, string Reference, string Exclusion, List<CreditProof> Proofs);
public sealed record ForeignCreditEvidence(int Version, int Year, List<CreditIncome> Income, List<CreditPayment> Payments);
/// <summary>按国家汇总本项目支持的分类所得限额，超限仅披露，不自动生成跨年抵免。</summary>
public sealed record CreditCountry(string Country, decimal IncomeCny, decimal LimitCny, decimal PaidCny,
    decimal ExcludedCny, decimal CreditCny, decimal ExcessCny);
public sealed record ForeignCreditAssessment(ForeignCreditEvidence Evidence, List<CreditCountry> Countries, decimal CreditCny);

/// <summary>结构化抵免复核：校验原币事实、凭证关系及国家限额，结果仍需核对申报折算口径。</summary>
public static class ForeignCreditReview
{
    public static ForeignCreditAssessment Assess(ForeignCreditEvidence document, int year,
        IReadOnlyList<CalculationResult> sources, IReadOnlyList<AppliedRate> rates)
    {
        if (document.Version != 1 || document.Year != year || document.Income is null || document.Payments is null
            || document.Income.Any(i => i is null) || document.Payments.Any(t => t is null))
            throw new CrsException("抵免明细版本或年度不正确。");
        if (sources.Any(s => s.Year != year || s.TaxInputs is null))
            throw new CrsException("来源任务缺少原币税务事实，请重新导入后复核抵免。");
        // 旧格式或跨范围的原币事实不能仅凭表面金额吻合取得抵免资格。
        if (sources.Any(s => s.TaxInputs!.Version != 1 || s.TaxInputs.RuleVersion != TaxEngine.PolicyVersion
            || s.TaxInputs.Broker != s.Broker || s.TaxInputs.Income.Any(i => i.Year != year || !s.CoveredAccounts.Contains(i.Account))
            || s.TaxInputs.ForeignTax.Any(t => t.Year != year || !s.CoveredAccounts.Contains(t.Account))))
            throw new CrsException("抵免来源事实版本、账户或年度不一致，请重新导入。");
        var facts = sources.SelectMany(s => s.TaxInputs!.Income.SelectMany(i => new[]
        {
            new CreditIncome(s.Broker, i.Account, "", "Dividend", i.Currency, i.Dividend),
            new CreditIncome(s.Broker, i.Account, "", "Interest", i.Currency, i.Interest)
        })).Where(i => i.Amount != 0).ToList();
        var paid = sources.SelectMany(s => s.TaxInputs!.ForeignTax.Select(t =>
            (s.Broker, t.Account, t.Currency, t.Amount))).ToList();
        if (facts.Any(i => i.Amount < 0) || paid.Any(t => t.Amount < 0))
            throw new CrsException("负收入或净退税需要核对原年度，当前抵免复核不支持自动处理。");
        foreach (var row in document.Income)
        {
            ValidateIdentity(row.Broker, row.Account, row.Country, row.Category, row.Currency, row.Amount);
            if (!facts.Any(f => Key(f) == Key(row))) throw new CrsException("所得分配包含未导入的账户、币种或所得项目。");
        }
        // 分配必须完整且原币严格一致，不能通过人为扩大收入增加国家抵免限额。
        foreach (var group in facts.GroupBy(Key))
            if (group.Sum(i => i.Amount) != document.Income.Where(i => Key(i) == group.Key).Sum(i => i.Amount))
                throw new CrsException("所得国家分配与导入的股息、利息原币金额不一致。");
        var references = new HashSet<string>(StringComparer.Ordinal);
        var proofOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tax in document.Payments)
        {
            ValidateIdentity(tax.Broker, tax.Account, tax.Country, tax.Category, tax.Currency, tax.Amount);
            if (string.IsNullOrWhiteSpace(tax.Authority) || string.IsNullOrWhiteSpace(tax.Reference) || tax.Exclusion is null || tax.Proofs is null
                || !references.Add($"{tax.Country}|{tax.Authority}|{tax.Reference}"))
                throw new CrsException("税款必须登记征税机关和唯一凭证编号。");
            if (!document.Income.Any(i => i.Broker == tax.Broker && i.Account == tax.Account && i.Country == tax.Country && i.Category == tax.Category))
                throw new CrsException("税款未关联同账户、国家和所得项目。");
            if (tax.Exclusion is not ("" or "Refunded" or "Penalty" or "TreatyDisallowed" or "WronglyLevied" or "Exempt"))
                throw new CrsException("抵免排除原因不受支持。");
            if (tax.Proofs.Any(p => p is null)) throw new CrsException("抵免凭证不能包含空记录。");
            var roles = tax.Proofs.Select(p => p.Role).ToHashSet(StringComparer.Ordinal);
            if (!roles.Contains("TaxReceipt") && !(roles.Contains("DeclarationOrNotice") && roles.Contains("Payment")))
                throw new CrsException("税款缺少缴税凭证，或申报表／通知书与支付凭证组合。");
            foreach (var proof in tax.Proofs)
            {
                if (proof.Role is not ("TaxReceipt" or "DeclarationOrNotice" or "Payment") || string.IsNullOrWhiteSpace(proof.Name)
                    || proof.Sha256 is null || proof.Sha256.Length != 64 || proof.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    throw new CrsException("凭证名称、类型或摘要无效。");
                // 同一原件不能借换编号重复抵免；共用凭证需先人工拆分并核对，当前入口明确拒绝。
                var identity = $"{tax.Country}|{tax.Authority}|{tax.Reference}";
                if (proofOwners.TryGetValue(proof.Sha256.ToUpperInvariant(), out var owner) && owner != identity)
                    throw new CrsException("同一凭证被用于多份税款，需先核对是否重复缴税。");
                proofOwners[proof.Sha256.ToUpperInvariant()] = identity;
            }
        }
        foreach (var group in document.Payments.GroupBy(t => (t.Broker, t.Account, t.Currency)))
            if (group.Sum(t => t.Amount) > paid.Where(t => (t.Broker, t.Account, t.Currency) == group.Key).Sum(t => t.Amount))
                throw new CrsException("凭证税款超过导入的同账户同币种净税款。");
        var countries = document.Income.Select(i => i.Country).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(country =>
        {
            // 同币种先合并再折算，避免人为拆分微小金额反复四舍五入抬高限额。
            var income = document.Income.Where(i => i.Country == country).GroupBy(i => i.Currency)
                .Sum(g => Money(g.Sum(i => i.Amount) * Rate(g.Key)));
            var limit = Money(income * .20m);
            var taxes = document.Payments.Where(t => t.Country == country).ToList();
            var eligible = taxes.Where(t => t.Exclusion == "").Sum(t => Money(t.Amount * Rate(t.Currency)));
            var excluded = taxes.Where(t => t.Exclusion != "").Sum(t => Money(t.Amount * Rate(t.Currency)));
            return new CreditCountry(country, income, limit, eligible, excluded, Math.Min(eligible, limit), Math.Max(eligible - limit, 0));
        }).ToList();
        return new(document, countries, countries.Sum(c => c.CreditCny));

        decimal Rate(string currency)
        {
            var values = rates.Where(r => r.Year == year && r.Currency == currency).Distinct().ToArray();
            if (values.Length != 1 || values[0].Value <= 0) throw new CrsException("抵免复核缺少唯一有效的冻结汇率。");
            return values[0].Value;
        }
    }

    /// <summary>生成派生任务，冻结新证据并保留原结果；尚未核对法定折算，不开放预计补税。</summary>
    public static CalculationResult Attach(CalculationResult source, ForeignCreditAssessment assessment)
    {
        var node = JsonSerializer.SerializeToNode(source)!.AsObject();
        var credit = assessment.CreditCny;
        if (credit > source.Summary.DividendInterestTax)
            throw new CrsException("国家分配的舍入与原收入税额存在差异，请核对分配及折算口径。");
        var summary = source.Summary with { ForeignCredit = credit,
            SupplementTax = Math.Max(source.Summary.DividendInterestTax - credit, 0) + source.Summary.GainTax, EstimatedTopUpCny = null };
        var snapshotId = Guid.NewGuid().ToString("N");
        node[nameof(CalculationResult.SnapshotId)] = snapshotId;
        node[nameof(CalculationResult.ParentSnapshotId)] = source.SnapshotId;
        node[nameof(CalculationResult.Summary)] = JsonSerializer.SerializeToNode(summary);
        node[nameof(CalculationResult.ForeignCreditAssessment)] = JsonSerializer.SerializeToNode(assessment);
        node[nameof(CalculationResult.EstimatedTopUpCny)] = null;
        node[nameof(CalculationResult.CalculationStatus)] = (int)CalculationStatus.Partial;
        node[nameof(CalculationResult.UsageLabel)] = (int)(source.Provisional ? UsageLabel.Provisional : UsageLabel.ReviewOnly);
        // 凭证复核属于新的输入类型，禁止沿用不含这些证据的旧重放快照。
        node[nameof(CalculationResult.IsReplayable)] = false;
        node[nameof(CalculationResult.InputRecoveryMode)] = (int)InputRecoveryMode.Unavailable;
        node[nameof(CalculationResult.CanonicalInputJson)] = "";
        node[nameof(CalculationResult.CanonicalInputDigest)] = "";
        var issues = source.Issues.Where(i => i.Code is not ("TAX_CREDIT_UNVERIFIED" or "TAX_CREDIT_REVIEW")).ToList();
        issues.Add(new("TAX_CREDIT_REVIEW", "国家分配及凭证摘要已登记；凭证真实性、税收协定、所得来源地与法定折算口径仍需人工核对。超限不自动结转，证券转让抵免未纳入。"));
        node[nameof(CalculationResult.Issues)] = JsonSerializer.SerializeToNode(issues);
        node[nameof(CalculationResult.SnapshotJson)] = JsonSerializer.Serialize(new
        {
            snapshotId, source.Year, source.Broker, parentSnapshotId = source.SnapshotId, createdUtc = DateTimeOffset.UtcNow, policy = "FOREIGN_CREDIT_REVIEW_V1",
            assessment, originalSnapshot = source.SnapshotJson, summary
        });
        return node.Deserialize<CalculationResult>()!;
    }

    private static (string, string, string, string) Key(CreditIncome i) => (i.Broker, i.Account, i.Category, i.Currency);
    private static decimal Money(decimal amount) => FifoEngine.Money(amount);
    private static void ValidateIdentity(string broker, string account, string country, string category, string currency, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(broker) || string.IsNullOrWhiteSpace(account) || country is null || country.Length != 2
            || country.Any(c => c is < 'A' or > 'Z') || category is not ("Dividend" or "Interest")
            || string.IsNullOrWhiteSpace(currency) || amount <= 0)
            throw new CrsException("抵免行需填写账户、两位大写国家／地区代码、Dividend／Interest、币种和正数金额。");
    }
}
