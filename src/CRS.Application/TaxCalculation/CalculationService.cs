using CRS.Application.Abstractions;
using System.Text;
using System.Text.Json;

namespace CRS.Application;

/// <summary>通过经校验的汇率配置创建计算编排服务。</summary>
public sealed class CalculationService(IExchangeRateProvider rates, IBrokerImporter importer,
    ICarryValidator carryValidator, ICalculationEvidence evidence, Action<string>? operationLog = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>执行年度导入、FIFO、税额及三项对账，并生成不可混用的来源快照。</summary>
    public CalculationResult Calculate(string[] files, int year, bool openingZero, bool scopeConfirmed,
        string? openingXml = null, CarryDocument? carry = null, CancellationToken cancellation = default,
        bool saveCanonicalSnapshot = false)
    {
        if (year is < 2000 or > 2100) throw new CrsException("年度必须在 2000 至 2100 之间。");
        if ((openingXml is not null || carry is not null) && openingZero || openingXml is not null && carry is not null)
            throw new CrsException("期初 XML、结转和期初无持仓确认只能选择一种。");
        operationLog?.Invoke($"开始计算；文件数量={files.Length}，年度={year}");
        cancellation.ThrowIfCancellationRequested();
        var data = importer.Parse(files, openingXml, cancellation);
        return CalculateParsed(data, files, year, openingZero, scopeConfirmed, openingXml, carry, cancellation,
            saveCanonicalSnapshot: saveCanonicalSnapshot);
    }

    /// <summary>使用已经规范化的领域输入执行统一计算，供原始导入和本地快照重放共用。</summary>
    private CalculationResult CalculateParsed(ImportData data, string[] files, int year, bool openingZero, bool scopeConfirmed,
        string? openingXml, CarryDocument? carry, CancellationToken cancellation, ReplayExecutionContext? replayContext = null,
        string? canonicalInputOverride = null, bool saveCanonicalSnapshot = false)
    {
        var prepared = new AnnualInputPreparation(carryValidator).Prepare(
            data, year, openingZero, scopeConfirmed, carry, cancellation);
        var begin = prepared.Begin; var end = prepared.End;
        var accounts = prepared.Accounts; var issues = prepared.Issues;
        var positionReconciled = prepared.PositionReconciled;
        var used = new Dictionary<string, AppliedRate>();
        // 查找汇率并登记实际使用值，未使用的币种不进入底稿。
        decimal Rate(int y, string c)
        {
            var r = replayContext is null ? rates.Get(y, c) : replayContext.GetRate(y, c);
            used[c] = r; return r.Value;
        }
        var engine = new FifoEngine();
        var matches = engine.Calculate(data.Trades, data.OpeningLots, data.OpeningRounding, issues, year, Rate, cancellation);
        issues.AddRange(engine.Issues);
        positionReconciled = AnnualReconciliation.CheckPositions(data, end, accounts, engine, issues, positionReconciled, cancellation);
        var income = data.Broker == "FUTU" ? data.AnnualIncome.Where(i => i.Year == year).Select(i => new Income(i.Account, i.Year, i.Currency, i.Dividend, i.Interest)).ToList()
            : data.Cash.Where(c => c.Date.Year == year).GroupBy(c => (c.Account, c.Currency)).Select(g =>
            new Income(g.Key.Account, year, g.Key.Currency, g.Where(c => c.Type == "Dividends").Sum(c => c.Amount),
                g.Where(c => c.Type is "Broker Interest Received" or "Bond Interest Received").Sum(c => c.Amount))).ToList();
        var withholding = data.Cash.Where(c => c.Date.Year == year && c.Type == "Withholding Tax").GroupBy(c => c.Currency).ToDictionary(g => g.Key, g => -g.Sum(c => c.Amount));
        if (withholding.Values.Any(v => v < 0)) issues.Add(new("NET_TAX_REFUND", "存在年度净退税，需核对原扣税年度；抵免不会为负数。"));
        // 没有应税现金的币种不触发无意义的汇率配置要求。
        income = income.Where(i => i.Dividend != 0 || i.Interest != 0).ToList();
        var summary = TaxEngine.Calculate(income, matches, withholding, year, Rate);
        // 保留未施加抵免上限的原币净税款，年度聚合只能汇总事实后统一计算。
        var taxInputs = new TaxCalculationInputs(1, TaxEngine.PolicyVersion, income,
            data.Cash.Where(c => c.Date.Year == year && c.Type == "Withholding Tax")
                .GroupBy(c => (c.Account, c.Currency))
                .Select(g => new ForeignTaxPayment(g.Key.Account, year, g.Key.Currency, -g.Sum(c => c.Amount))).ToList(), data.Broker);
        var (pnlChecks, cashChecks) = AnnualReconciliation.CheckSalesAndCash(data, year, begin, end, matches, issues, cancellation);
        foreach (var r in used.Values.Where(r => !r.SourceConfirmed)) issues.Add(new("RATE_SOURCE", "实际使用的汇率尚未登记来源。", Currency: r.Currency));
        var clock = timeProvider ?? TimeProvider.System;
        var provisional = replayContext?.Provisional ?? (year >= clock.GetLocalNow().Year || used.Values.Any(r => r.Provisional));
        var finalIssues = issues.Distinct().ToList();
        var (calculationStatus, completeness, reconciliation, usage, estimated) = ResultStatusPolicy.Evaluate(
            finalIssues, scopeConfirmed, positionReconciled, cashChecks, pnlChecks, provisional, used.Values, summary, data.Broker != "FUTU");
        summary = summary with { EstimatedTopUpCny = estimated };
        // 规范化快照同时保存排序后的领域记录，使历史结果在原文件暂时不可用时仍可重放审计。
        var fileRefs = files.Select(path => new { name = Path.GetFileName(path), sha256 = evidence.FileHash(path) }).OrderBy(x => x.name).ToArray();
        var openingRef = openingXml is null ? null : new { name = Path.GetFileName(openingXml), sha256 = evidence.FileHash(openingXml) };
        object canonicalValue = saveCanonicalSnapshot ? new
        {
            schema = "CRS.CanonicalInput.v1", files = fileRefs, opening = openingRef,
            broker = data.Broker, annualIncome = data.AnnualIncome, fundMovements = data.FundMovements,
            tradeAmountChecks = data.TradeAmountChecks, securityMovements = data.SecurityMovements, beginningPositions = data.BeginningPositions,
            trades = data.Trades.OrderBy(t => t.Key.Account).ThenBy(t => t.Key.Instrument).ThenBy(t => t.Time).ThenBy(t => t.Id),
            reportedSales = data.ReportedSales.OrderBy(t => t.Key.Account).ThenBy(t => t.Time).ThenBy(t => t.Id),
            cash = data.Cash.OrderBy(c => c.Account).ThenBy(c => c.Date).ThenBy(c => c.Id),
            openingLots = data.OpeningLots.OrderBy(l => l.Key.Account).ThenBy(l => l.Key.Instrument).ThenBy(l => l.BuyTime).ThenBy(l => l.RecordId),
            positions = data.Positions.OrderBy(p => p.Key.Account).ThenBy(p => p.Key.Instrument).ThenBy(p => p.Date),
            sources = data.Sources.OrderBy(s => s.Account).ThenBy(s => s.Start).ThenBy(s => s.File),
            openingRounding = data.OpeningRounding.OrderBy(r => r.Key.Account).ThenBy(r => r.Key.Instrument),
            cashChecks = data.CashChecks.OrderBy(c => c.Account).ThenBy(c => c.Currency).ThenBy(c => c.Start),
            warnings = data.Warnings.Order(), issues = data.Issues.OrderBy(i => i.Code).ThenBy(i => i.RecordId),
            confirmations = new { openingZero, scopeConfirmed, taxpayerScopeId = "LOCAL_USER" }
        } : new
        {
            schema = "CRS.InputReferences.v1", files = fileRefs, opening = openingRef,
            confirmations = new { openingZero, scopeConfirmed, taxpayerScopeId = "LOCAL_USER" }
        };
        var canonical = canonicalInputOverride ?? JsonSerializer.Serialize(canonicalValue);
        var canonicalDigest = evidence.Hash(Encoding.UTF8.GetBytes(canonical));
        var result = new CalculationResult { Broker = data.Broker, ImportedTradeCount = data.Trades.Count(t => t.Time.Year == year),
            SessionTrades = data.Trades.Where(t => t.Time.Year == year).ToList(), AnnualIncome = data.AnnualIncome, FundMovements = data.FundMovements.Where(f => f.Date.Year == year).ToList(),
            TradeAmountChecks = data.TradeAmountChecks.Where(c => data.Trades.Any(t => t.Id == c.TradeId && t.Time.Year == year)).ToList(),
            SecurityMovements = data.SecurityMovements, Year = year, Summary = summary, TaxInputs = taxInputs, Matches = matches, Issues = finalIssues, Warnings = data.Warnings,
            CashChecks = cashChecks, PnlChecks = pnlChecks, Rates = used.Values.OrderBy(r => r.Currency).ToList(), Sources = data.Sources,
            EndingLots = engine.EndingLots, EndingRounding = engine.EndingRounding, OpeningZero = openingZero, ScopeConfirmed = scopeConfirmed,
            Provisional = provisional, Reconciled = data.Broker != "FUTU" && positionReconciled && pnlChecks.All(p => p.Status == "一致") && cashChecks.Count > 0 && cashChecks.All(c => c.Status == "一致"),
            CarryEligible = positionReconciled && engine.Uncertain.Count == 0 && !provisional && !finalIssues.Any(i => i.AffectsCost || i.Code is "INCOMPLETE_PERIOD" or "OPENING_UNCONFIRMED" or "SCOPE_UNCONFIRMED" or "UNSUPPORTED_POSITION"),
            CalculationStatus = calculationStatus, DataCompleteness = completeness, ReconciliationStatus = reconciliation, UsageLabel = usage,
            CoveredAccounts = accounts.ToList(), UnknownGainCount = finalIssues.Count(i => i.Code == "MISSING_COST"), EstimatedTopUpCny = estimated,
            // 原始导入要求重新提供文件；从完整规范化快照进入时才标记为可重放。
            InputRecoveryMode = canonicalInputOverride is not null || saveCanonicalSnapshot ? InputRecoveryMode.CanonicalLocalSnapshot : InputRecoveryMode.ReimportOriginalFiles,
            CanonicalInputDigest = canonicalDigest, CanonicalInputJson = canonical, IsReplayable = canonicalInputOverride is not null || saveCanonicalSnapshot };
        result.SnapshotId = Guid.NewGuid().ToString("N");
        // 快照保留实际输入指纹和口径，服务令牌从不进入计算模型。
        result.SnapshotJson = JsonSerializer.Serialize(new { result.SnapshotId, year, createdUtc = clock.GetUtcNow(),
            policy = TaxEngine.PolicyVersion, algorithm = ReplayExecutionContext.SupportedAlgorithm, version = typeof(CalculationService).Assembly.GetName().Version?.ToString(),
            configuration = replayContext?.Configuration ?? rates.Fingerprint, sources = data.Sources, rates = result.Rates, openingZero, scopeConfirmed,
            calculationStatus = result.CalculationStatus, dataCompleteness = result.DataCompleteness, reconciliationStatus = result.ReconciliationStatus,
            usageLabel = result.UsageLabel, taxpayerScopeId = result.TaxpayerScopeId, coveredAccounts = result.CoveredAccounts,
            inputRecoveryMode = result.InputRecoveryMode, canonicalInputDigest = result.CanonicalInputDigest, isReplayable = result.IsReplayable,
            carry = carry?.SourceSnapshotId, carryFingerprint = carry is null ? null : evidence.HashJson(carry),
            coreAssembly = evidence.FileHash(typeof(FifoEngine).Assembly.Location),
            infrastructureAssembly = evidence.InfrastructureAssemblyHash, result.Complete, summary });
        operationLog?.Invoke($"计算结束；匹配批次数={matches.Count}，待复核数={result.Issues.Count}");
        return result;
    }

    /// <summary>从完整规范化快照重放计算，使用历史汇率快照而不是当前配置。</summary>
    public CalculationResult Replay(CalculationResult snapshot, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!snapshot.IsReplayable || snapshot.InputRecoveryMode != InputRecoveryMode.CanonicalLocalSnapshot)
            throw new CrsException("该历史结果未保存可重放的规范化快照。");
        var digest = evidence.Hash(Encoding.UTF8.GetBytes(snapshot.CanonicalInputJson));
        if (digest != snapshot.CanonicalInputDigest) throw new CrsException("规范化输入快照摘要不一致，不能重放。");
        var document = JsonSerializer.Deserialize<CanonicalInputDocument>(snapshot.CanonicalInputJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new CrsException("规范化输入快照格式无效。");
        if (document.Schema != "CRS.CanonicalInput.v1") throw new CrsException("规范化输入快照版本不受当前程序支持。");
        var replayContext = ReplayExecutionContext.Read(snapshot);
        var data = document.ToImportData();
        cancellation.ThrowIfCancellationRequested();
        var replay = CalculateParsed(data, [], snapshot.Year, document.Confirmations.OpeningZero,
            document.Confirmations.ScopeConfirmed, null, null, cancellation, replayContext, snapshot.CanonicalInputJson, true);
        if (replay.CanonicalInputDigest != snapshot.CanonicalInputDigest)
            throw new CrsException("重放后的输入摘要不一致，历史结果不能被静默替换。");
        return replay;
    }

    /// <summary>按历史规范化快照校验重新选择的原始文件，防止用错年度或被修改的报告。</summary>
    public void VerifyOriginalFiles(CalculationResult snapshot, IEnumerable<string> files, string? opening = null)
    {
        if (snapshot.InputRecoveryMode is not (InputRecoveryMode.ReimportOriginalFiles or InputRecoveryMode.CanonicalLocalSnapshot)
            || string.IsNullOrWhiteSpace(snapshot.CanonicalInputJson))
            throw new CrsException("当前计算没有可用于原始文件校验的输入摘要。");
        using var document = JsonDocument.Parse(snapshot.CanonicalInputJson);
        var expected = document.RootElement.GetProperty("files").EnumerateArray()
            .Select(e => (Name: e.GetProperty("name").GetString() ?? "", Sha: e.GetProperty("sha256").GetString() ?? ""))
            .OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Sha, StringComparer.Ordinal).ToArray();
        var actualPaths = files.ToArray();
        var actual = actualPaths.Select(path => (Name: Path.GetFileName(path), Sha: evidence.FileHash(path)))
            .OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Sha, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual)) throw new CrsException("重新选择的活动报告与历史计算摘要不一致，请使用原始文件。");
        var expectedOpening = document.RootElement.GetProperty("opening");
        if (expectedOpening.ValueKind == JsonValueKind.Null)
        {
            if (opening is not null) throw new CrsException("历史计算未使用期初文件，不能额外加入期初来源。");
            return;
        }
        if (opening is null) throw new CrsException("历史计算使用了期初文件，请重新选择同一份文件。");
        var openingName = expectedOpening.GetProperty("name").GetString() ?? "";
        var openingSha = expectedOpening.GetProperty("sha256").GetString() ?? "";
        if (Path.GetFileName(opening) != openingName || evidence.FileHash(opening) != openingSha)
            throw new CrsException("重新选择的期初文件与历史计算摘要不一致，请使用原始 LOT 文件。");
    }
}
