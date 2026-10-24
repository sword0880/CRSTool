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
        bool saveCanonicalSnapshot = false,IReadOnlyList<AccountPeriodEvidence>? accountPeriods=null)
    {
        if (year is < 2000 or > 2100) throw new CrsException("年度必须在 2000 至 2100 之间。");
        if ((openingXml is not null || carry is not null) && openingZero || openingXml is not null && carry is not null)
            throw new CrsException("期初 XML、结转和期初无持仓确认只能选择一种。");
        // 固定本次文件选择；同名来源不能通过目录不同绕过快照追溯。
        files=files.ToArray();
        if(files.GroupBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase)
                .Any(g=>g.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)
            || openingXml is not null && files.Any(p=>string.Equals(Path.GetFileName(p),Path.GetFileName(openingXml),StringComparison.OrdinalIgnoreCase)))
            throw new CrsException("源文件名重复，请保留唯一文件名以便追溯活动报告和期初资料。");
        operationLog?.Invoke($"开始计算；文件数量={files.Length}，年度={year}");
        cancellation.ThrowIfCancellationRequested();
        var data = importer.Parse(files, openingXml, cancellation);
        if(accountPeriods is not null) data.AccountPeriods.AddRange(accountPeriods);
        // IBKR 允许重复选择同一原件，解析器只读取一次，引用清单也只记录一次。
        files=files.DistinctBy(Path.GetFullPath,StringComparer.OrdinalIgnoreCase).ToArray();
        var result=CalculateParsed(data, files, year, openingZero, scopeConfirmed, openingXml, carry, cancellation,
            saveCanonicalSnapshot: saveCanonicalSnapshot);
        result.SealForPersistence(evidence.InfrastructureAssemblyHash); return result.Freeze();
    }

    /// <summary>使用已经规范化的领域输入执行统一计算，供原始导入和本地快照重放共用。</summary>
    private CalculationResult CalculateParsed(ImportData data, string[] files, int year, bool openingZero, bool scopeConfirmed,
        string? openingXml, CarryDocument? carry, CancellationToken cancellation, ReplayExecutionContext? replayContext = null,
        string? canonicalInputOverride = null, bool saveCanonicalSnapshot = false)
    {
        // 保存合入结转前的原始期初记录；完整快照另外保存结转，重放只合入一次。
        var rawOpeningLots = FifoOrdering.Lots(data.OpeningLots).Select(l => l.Copy()).ToArray();
        var rawOpeningRounding = data.OpeningRounding.ToArray();
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
        // 券商预扣税不是自动取得抵免资格；缺少国家分配和凭证时关闭年度预计补税。
        if (withholding.Values.Any(v => v != 0)) issues.Add(new("TAX_CREDIT_UNVERIFIED",
            "境外预扣税尚未关联国家／地区、所得项目和缴税凭证；现有抵免金额仅供辅助核对，请在历史任务导入抵免明细。"));
        // 保留未施加抵免上限的原币净税款，年度聚合只能汇总事实后统一计算。
        var taxInputs = new TaxCalculationInputs(1, TaxEngine.PolicyVersion, income,
            data.Cash.Where(c => c.Date.Year == year && c.Type == "Withholding Tax")
                .GroupBy(c => (c.Account, c.Currency))
                .Select(g => new ForeignTaxPayment(g.Key.Account, year, g.Key.Currency, -g.Sum(c => c.Amount))).ToList(), data.Broker);
        var (pnlChecks, cashChecks) = AnnualReconciliation.CheckSalesAndCash(data, year, begin, end, matches, issues, cancellation);
        foreach (var r in used.Values.Where(r => !r.SourceConfirmed)) issues.Add(new("RATE_SOURCE", "实际使用的汇率尚未登记来源。", Currency: r.Currency));
        var clock = timeProvider ?? TimeProvider.System;
        // 业务年度固定使用北京时间；宿主时区不能改变同一时刻的临时结果状态。
        var provisional = replayContext?.Provisional ?? (year >= CRS.Timing.BeijingClock.Read(clock).Year || used.Values.Any(r => r.Provisional));
        var finalIssues = issues.Distinct().ToList();
        var (calculationStatus, completeness, reconciliation, usage, estimated) = ResultStatusPolicy.Evaluate(
            finalIssues, scopeConfirmed, positionReconciled, cashChecks, pnlChecks, provisional, used.Values, summary, data.Broker != "FUTU");
        summary = summary with { EstimatedTopUpCny = estimated };
        // 规范化快照同时保存排序后的领域记录，使历史结果在原文件暂时不可用时仍可重放审计。
        // 指纹采用解析器返回的冻结来源，不在计算结束后重新打开可能已被替换的原件。
        string SourceHash(string path,bool opening)
        {
            var hashes=data.Sources.Where(s=>s.File==Path.GetFileName(path) && s.Opening==opening)
                .Select(s=>s.Sha256.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
            if(hashes.Length!=1 || string.IsNullOrWhiteSpace(hashes[0]))
                throw new CrsException("解析来源缺少唯一内容摘要，不能生成可信输入快照。");
            return hashes[0];
        }
        var fileRefs = files.Select(path => new { name = Path.GetFileName(path), sha256 = SourceHash(path,false) })
            .OrderBy(x => x.name, StringComparer.Ordinal).ThenBy(x => x.sha256, StringComparer.Ordinal).ToArray();
        var openingRef = openingXml is null ? null : new { name = Path.GetFileName(openingXml), sha256 = SourceHash(openingXml,true) };
        object canonicalValue = saveCanonicalSnapshot ? new
        {
            schema = "CRS.CanonicalInput.v2", files = fileRefs, opening = openingRef, carry,
            broker = data.Broker, annualIncome = StableRows(data.AnnualIncome), fundMovements = StableRows(data.FundMovements),
            tradeAmountChecks = StableRows(data.TradeAmountChecks), securityMovements = StableRows(data.SecurityMovements), beginningPositions = StableRows(data.BeginningPositions),
            trades = FifoOrdering.Trades(data.Trades),
            reportedSales = FifoOrdering.Trades(data.ReportedSales),
            cash = StableRows(data.Cash),
            openingLots = rawOpeningLots,
            positions = StableRows(data.Positions), sources = StableRows(data.Sources),
            openingRounding = StableRows(rawOpeningRounding), cashChecks = StableRows(data.CashChecks),
            warnings = data.Warnings.Order(StringComparer.Ordinal), issues = StableRows(data.Issues),
            confirmations = new { openingZero, scopeConfirmed, taxpayerScopeId = "LOCAL_USER" }
        } : new
        {
            schema = "CRS.InputReferences.v1", files = fileRefs, opening = openingRef,
            confirmations = new { openingZero, scopeConfirmed, taxpayerScopeId = "LOCAL_USER" }
        };
        var canonical = canonicalInputOverride ?? JsonSerializer.Serialize(canonicalValue);
        if(canonicalInputOverride is null && data.AccountPeriods.Count>0)
        {
            // 新契约显式保存期间依据；不能向旧 v2／引用 v1 快照偷偷添加字段。
            var node=System.Text.Json.Nodes.JsonNode.Parse(canonical)!.AsObject();
            node["schema"]=saveCanonicalSnapshot?"CRS.CanonicalInput.v3":"CRS.InputReferences.v2";
            node["accountPeriods"]=JsonSerializer.SerializeToNode(data.AccountPeriods.OrderBy(p=>p.Account,StringComparer.Ordinal));
            canonical=node.ToJsonString();
        }
        var canonicalDigest = evidence.Hash(Encoding.UTF8.GetBytes(canonical));
        var result = new CalculationResult { Broker = data.Broker, ImportProvenance=TaxImportProvenance.Merge(data.ImportProvenance??[]), ImportedTradeCount = data.Trades.Count(t => t.Time.Year == year),
            AccountPeriods=data.AccountPeriods.Count==0?null:(SnapshotList<AccountPeriodEvidence>)data.AccountPeriods.OrderBy(p=>p.Account,StringComparer.Ordinal).ToList(),
            AccountCoverage=data.AccountPeriods.Count==0?null:(SnapshotList<AccountCoverage>)accounts.Select(account=>
            {
                var window=AccountPeriodPolicy.Window(data,account,begin,end);
                return new AccountCoverage(account,window.Start,window.End,provisional?"Provisional":window.Start==begin && window.End==end?"CalendarYear":"AccountActivity",
                    !finalIssues.Any(i=>i.Code=="INCOMPLETE_PERIOD" && i.Account==account));
            }).ToList(),
            SessionTrades = FifoOrdering.Trades(data.Trades.Where(t => t.Time.Year == year)).ToList(), AnnualIncome = data.AnnualIncome, FundMovements = data.FundMovements.Where(f => f.Date.Year == year).ToList(),
            TradeAmountChecks = data.TradeAmountChecks.Where(c => data.Trades.Any(t => t.Id == c.TradeId && t.Time.Year == year)).ToList(),
            SecurityMovements = data.SecurityMovements, Year = year, Summary = summary, TaxInputs = taxInputs, Matches = matches, Issues = finalIssues, Warnings = data.Warnings,
            CashChecks = cashChecks, PnlChecks = pnlChecks, Rates = used.Values.OrderBy(r => r.Currency).ToList(), Sources = data.Sources,
            EndingLots = engine.EndingLots, EndingRounding = engine.EndingRounding, OpeningZero = openingZero, ScopeConfirmed = scopeConfirmed,
            Provisional = provisional, Reconciled = data.Broker != "FUTU" && positionReconciled && pnlChecks.All(p => p.Status == "一致") && cashChecks.Count > 0 && cashChecks.All(c => c.Status == "一致"),
            CarryEligible = positionReconciled && engine.Uncertain.Count == 0 && !provisional && !data.AccountPeriods.Any(p=>p.Closed is {} close && close<=end)
                && !finalIssues.Any(i => i.AffectsCost || i.Code is "INCOMPLETE_PERIOD" or "OPENING_UNCONFIRMED" or "SCOPE_UNCONFIRMED" or "UNSUPPORTED_POSITION"),
            CalculationStatus = calculationStatus, DataCompleteness = completeness, ReconciliationStatus = reconciliation, UsageLabel = usage,
            CoveredAccounts = accounts.ToList(), UnknownGainCount = finalIssues.Count(i => i.Code == "MISSING_COST"), EstimatedTopUpCny = estimated,
            // 原始导入要求重新提供文件；从完整规范化快照进入时才标记为可重放。
            InputRecoveryMode = canonicalInputOverride is not null || saveCanonicalSnapshot ? InputRecoveryMode.CanonicalLocalSnapshot : InputRecoveryMode.ReimportOriginalFiles,
            CanonicalInputDigest = canonicalDigest, CanonicalInputJson = canonical, IsReplayable = canonicalInputOverride is not null || saveCanonicalSnapshot };
        result.SnapshotId = Guid.NewGuid().ToString("N");
        // 快照保留实际输入指纹和口径，服务令牌从不进入计算模型。
        result.SnapshotJson = JsonSerializer.Serialize(new { result.SnapshotId, year, createdUtc = CRS.Timing.BeijingClock.Read(clock),
            policy = TaxEngine.PolicyVersion, algorithm = ReplayExecutionContext.SupportedAlgorithm, version = typeof(CalculationService).Assembly.GetName().Version?.ToString(),
            configuration = replayContext?.Configuration ?? rates.Fingerprint, sources = data.Sources, rates = result.Rates, openingZero, scopeConfirmed,
            calculationStatus = result.CalculationStatus, dataCompleteness = result.DataCompleteness, reconciliationStatus = result.ReconciliationStatus,
            usageLabel = result.UsageLabel, taxpayerScopeId = result.TaxpayerScopeId, coveredAccounts = result.CoveredAccounts,
            inputRecoveryMode = result.InputRecoveryMode, canonicalInputDigest = result.CanonicalInputDigest, isReplayable = result.IsReplayable,
            carry = carry?.SourceSnapshotId, carryFingerprint = carry is null ? null : evidence.HashJson(carry),
            coreAssembly = evidence.FileHash(typeof(FifoEngine).Assembly.Location),
            infrastructureAssembly = evidence.InfrastructureAssemblyHash, result.Complete, summary });
        // 新生成的快照也遵守相同契约，超限或结构错误不得先保存为不可读取的历史任务。
        using var validatedInput=CanonicalInputReader.Read(result,saveCanonicalSnapshot);
        operationLog?.Invoke($"计算结束；匹配批次数={matches.Count}，待复核数={result.Issues.Count}");
        return result;
    }

    /// <summary>快照其他集合以完整记录的序列化键稳定排序，避免相同局部键仍依赖文件选择顺序。</summary>
    private static IEnumerable<T> StableRows<T>(IEnumerable<T> rows) => rows.OrderBy(row => JsonSerializer.Serialize(row), StringComparer.Ordinal);

    /// <summary>从完整规范化快照重放计算，使用历史汇率快照而不是当前配置。</summary>
    public CalculationResult Replay(CalculationResult snapshot, CancellationToken cancellation = default)
    {
        RunEvidenceSeal.Verify(snapshot);
        if(snapshot.EvidenceSeal is { } seal && (seal.ApplicationSha256!=RunEvidenceSeal.AssemblyHash(typeof(CalculationService).Assembly.Location)
            || seal.DomainSha256!=RunEvidenceSeal.AssemblyHash(typeof(FifoEngine).Assembly.Location) || seal.InfrastructureSha256!=evidence.InfrastructureAssemblyHash))
            throw new CrsException("历史执行构建与当前版本不同，不能用新程序冒充原版本复算；仍可查看冻结结果。");
        cancellation.ThrowIfCancellationRequested();
        if (!snapshot.IsReplayable || snapshot.InputRecoveryMode != InputRecoveryMode.CanonicalLocalSnapshot)
            throw new CrsException("该历史结果未保存可重放的规范化快照。");
        // 复算、明细展示及原件校验共用同一套有界契约检查。
        var document=CanonicalInputReader.ReadFull(snapshot);
        var replayContext = ReplayExecutionContext.Read(snapshot);
        var data = document.ToImportData();
        data.ImportProvenance=snapshot.ImportProvenance?.ToList();
        cancellation.ThrowIfCancellationRequested();
        var replay = CalculateParsed(data, [], snapshot.Year, document.Confirmations.OpeningZero,
            document.Confirmations.ScopeConfirmed, null, document.Carry, cancellation, replayContext, snapshot.CanonicalInputJson, true);
        if (replay.CanonicalInputDigest != snapshot.CanonicalInputDigest)
            throw new CrsException("重放后的输入摘要不一致，历史结果不能被静默替换。");
        // 全量业务输出一致才允许作为复算结果；时间、运行编号和父运行不参与金额比对。
        ReplayResultComparison.Verify(snapshot, replay);
        replay.ParentSnapshotId = snapshot.SnapshotId;
        var audit = System.Text.Json.Nodes.JsonNode.Parse(replay.SnapshotJson)!.AsObject();
        audit["parentSnapshotId"] = snapshot.SnapshotId;
        replay.SnapshotJson = audit.ToJsonString();
        replay.SealForPersistence(evidence.InfrastructureAssemblyHash); return replay.Freeze();
    }

    /// <summary>按历史规范化快照校验重新选择的原始文件，防止用错年度或被修改的报告。</summary>
    public void VerifyOriginalFiles(CalculationResult snapshot, IEnumerable<string> files, string? opening = null)
    {
        if (snapshot.InputRecoveryMode is not (InputRecoveryMode.ReimportOriginalFiles or InputRecoveryMode.CanonicalLocalSnapshot)
            || string.IsNullOrWhiteSpace(snapshot.CanonicalInputJson))
            throw new CrsException("当前计算没有可用于原始文件校验的输入摘要。");
        using var document = CanonicalInputReader.Read(snapshot);
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
