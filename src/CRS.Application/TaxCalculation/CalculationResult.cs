using System.Text.Json.Serialization;

namespace CRS.Application;

public sealed class CalculationResult
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public SnapshotList<TaxImportProvenance>? ImportProvenance {get;init;}
    public string Broker { get; init; } = "IBKR";
    // 缺失的新字段不写入旧载荷，避免给旧封存擅自补造期间依据。
    // 旧任务省略新增空字段，保持原封存载荷；活动期完整不能等同税额已签认。
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public SnapshotList<AccountPeriodEvidence>? AccountPeriods {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public SnapshotList<AccountCoverage>? AccountCoverage {get;init;}
    /// <summary>只保留笔数；交易原始明细默认仅存在于本次会话。</summary>
    public int? ImportedTradeCount { get; init; }
    [JsonIgnore] public SnapshotList<Trade> SessionTrades { get; init; } = [];
    public SnapshotList<AnnualIncome> AnnualIncome { get; init; } = [];
    public SnapshotList<FundMovement> FundMovements { get; init; } = [];
    public SnapshotList<TradeAmountCheck> TradeAmountChecks { get; init; } = [];
    public SnapshotList<SecurityMovement> SecurityMovements { get; init; } = [];
    public required int Year { get; init; }
    public required TaxSummary Summary { get; init; }
    /// <summary>原币年度事实；旧历史记录缺失时不得据其税额生成完整年度聚合。</summary>
    public TaxCalculationInputs? TaxInputs { get; init; }
    /// <summary>结构化境外抵免复核及冻结凭证关系；旧历史缺失时不推定已核实。</summary>
    public ForeignCreditAssessment? ForeignCreditAssessment { get; init; }
    public SnapshotList<Match> Matches { get; init; } = [];
    public SnapshotList<Issue> Issues { get; init; } = [];
    public SnapshotList<string> Warnings { get; init; } = [];
    public SnapshotList<CashReconciliation> CashChecks { get; init; } = [];
    public SnapshotList<PnlReconciliation> PnlChecks { get; init; } = [];
    public SnapshotList<AppliedRate> Rates { get; init; } = [];
    public SnapshotList<CostLot> EndingLots { get; init; } = [];
    public SnapshotList<RoundingState> EndingRounding { get; init; } = [];
    public SnapshotList<SourceReport> Sources { get; init; } = [];
    public bool Provisional { get; init; }
    public bool Reconciled { get; init; }
    public bool CarryEligible { get; init; }
    public bool OpeningZero { get; init; }
    public bool ScopeConfirmed { get; init; }
    /// <summary>四维结果状态，导出和界面必须使用同一状态策略。</summary>
    public CalculationStatus CalculationStatus { get; init; } = CalculationStatus.Partial;
    public DataCompleteness DataCompleteness { get; init; } = DataCompleteness.Incomplete;
    public ReconciliationStatus ReconciliationStatus { get; init; } = ReconciliationStatus.NotVerifiable;
    public UsageLabel UsageLabel { get; init; } = UsageLabel.ReviewOnly;
    /// <summary>纳税人范围标识及纳入的账户，防止不同账户被无意混算。</summary>
    public string TaxpayerScopeId { get; init; } = "LOCAL_USER";
    public SnapshotList<string> CoveredAccounts { get; init; } = [];
    public int UnknownGainCount { get; init; }
    /// <summary>只在年度完整且结果状态允许时填充；不确定时保持 null 而非 0。</summary>
    public decimal? EstimatedTopUpCny { get; init; }
    public InputRecoveryMode InputRecoveryMode { get; init; } = InputRecoveryMode.Unavailable;
    public string CanonicalInputDigest { get; init; } = "";
    /// <summary>规范化输入快照仍含账户和交易事实；不保存 Flex 服务令牌，不代表匿名化。</summary>
    public string CanonicalInputJson { get; init; } = "";
    public bool IsReplayable { get; init; }
    private bool frozen;
    private string snapshotId="",snapshotJson="";
    private string? parentSnapshotId;
    private RunEvidenceSeal? evidenceSeal;
    public RunEvidenceSeal? EvidenceSeal {get=>evidenceSeal;set {CheckMutable(); evidenceSeal=value;}}
    [JsonIgnore] public bool IsFrozen=>frozen;
    public string SnapshotId { get=>snapshotId; set {CheckMutable(); snapshotId=value;} }
    /// <summary>复算另存新运行并保留父运行，不覆盖原始冻结结果。</summary>
    public string? ParentSnapshotId { get=>parentSnapshotId; set {CheckMutable(); parentSnapshotId=value;} }
    /// <summary>年度汇总的来源任务，随结果持久化并在底稿中披露。</summary>
    public SnapshotList<AggregationSource> AggregationSources { get; init; } = [];
    [JsonIgnore] public bool IsAnnualAggregate => AggregationSources.Count > 0;
    public string SnapshotJson { get=>snapshotJson; set {CheckMutable(); snapshotJson=value;} }
    public bool Complete => CalculationStatus == CalculationStatus.Completed && Issues.Count == 0 && Reconciled && !Provisional;
    /// <summary>发布时冻结全部业务集合和可变成本副本；新任务需复制，不能复活旧结果。</summary>
    public CalculationResult Freeze()
    {
        if(frozen) return this;
        TaxImportProvenance.Validate(Sources,ImportProvenance,Broker);
        ImportProvenance?.Freeze();
        AccountPeriods?.Freeze(); AccountCoverage?.Freeze();
        for(var i=0;i<EndingLots.Count;i++) {var lot=EndingLots[i].Copy(); lot.Freeze(); EndingLots[i]=lot;}
        SessionTrades.Freeze(); AnnualIncome.Freeze(); FundMovements.Freeze(); TradeAmountChecks.Freeze(); SecurityMovements.Freeze();
        Matches.Freeze(); Issues.Freeze(); Warnings.Freeze(); CashChecks.Freeze(); PnlChecks.Freeze(); Rates.Freeze(); Sources.Freeze();
        EndingLots.Freeze(); EndingRounding.Freeze(); CoveredAccounts.Freeze();
        if(TaxInputs is { } facts) {facts.Income.Freeze(); facts.ForeignTax.Freeze();}
        if(ForeignCreditAssessment is { } credit)
        {
            credit.Countries.Freeze(); credit.Evidence.Income.Freeze();
            foreach(var payment in credit.Evidence.Payments) payment.Proofs.Freeze();
            credit.Evidence.Payments.Freeze();
        }
        foreach(var source in AggregationSources) source.Accounts.Freeze();
        AggregationSources.Freeze(); frozen=true; return this;
    }
    /// <summary>持久化时补齐旧构建结果的封存，不允许重封一个已经有证据的结果。</summary>
    public void SealForPersistence(string infrastructureSha256)
    {
        if(evidenceSeal is null) evidenceSeal=RunEvidenceSeal.Create(this,infrastructureSha256);
        else RunEvidenceSeal.Verify(this);
    }
    private void CheckMutable() {if(frozen) throw new CrsException("计算结果已冻结，请另存新任务，不能覆盖历史执行信息。");}
}
