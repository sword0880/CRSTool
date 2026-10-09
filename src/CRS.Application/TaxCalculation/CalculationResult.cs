using System.Text.Json.Serialization;

namespace CRS.Application;

public sealed class CalculationResult
{
    public string Broker { get; init; } = "IBKR";
    /// <summary>只保留笔数；交易原始明细默认仅存在于本次会话。</summary>
    public int? ImportedTradeCount { get; init; }
    [JsonIgnore] public List<Trade> SessionTrades { get; init; } = [];
    public List<AnnualIncome> AnnualIncome { get; init; } = [];
    public List<FundMovement> FundMovements { get; init; } = [];
    public List<TradeAmountCheck> TradeAmountChecks { get; init; } = [];
    public List<SecurityMovement> SecurityMovements { get; init; } = [];
    public required int Year { get; init; }
    public required TaxSummary Summary { get; init; }
    /// <summary>原币年度事实；旧历史记录缺失时不得据其税额生成完整年度聚合。</summary>
    public TaxCalculationInputs? TaxInputs { get; init; }
    public List<Match> Matches { get; init; } = [];
    public List<Issue> Issues { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<CashReconciliation> CashChecks { get; init; } = [];
    public List<PnlReconciliation> PnlChecks { get; init; } = [];
    public List<AppliedRate> Rates { get; init; } = [];
    public List<CostLot> EndingLots { get; init; } = [];
    public List<RoundingState> EndingRounding { get; init; } = [];
    public List<SourceReport> Sources { get; init; } = [];
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
    public List<string> CoveredAccounts { get; init; } = [];
    public int UnknownGainCount { get; init; }
    /// <summary>只在年度完整且结果状态允许时填充；不确定时保持 null 而非 0。</summary>
    public decimal? EstimatedTopUpCny { get; init; }
    public InputRecoveryMode InputRecoveryMode { get; init; } = InputRecoveryMode.Unavailable;
    public string CanonicalInputDigest { get; init; } = "";
    /// <summary>规范化输入快照；只保存已脱敏的领域记录，不保存 Flex 服务令牌。</summary>
    public string CanonicalInputJson { get; init; } = "";
    public bool IsReplayable { get; init; }
    public string SnapshotId { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public bool Complete => CalculationStatus == CalculationStatus.Completed && Issues.Count == 0 && Reconciled && !Provisional;
}
