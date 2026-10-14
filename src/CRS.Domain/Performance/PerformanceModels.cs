namespace CRS.Domain.Performance;

// 绩效拥有独立口径及版本；账户内部键可以包含原账号，但界面只能展示 Alias。
public enum PerformanceQuality { Verified, Reconstructed, Estimated, InsufficientData }
public enum InvestmentEventKind { Deposit, Withdrawal, CashTransfer, SecurityTransfer, Buy, Sell, Dividend, Interest, WithholdingTax, Fee, FinancingInterest, FxExchange, Borrowing, Split, Unsupported, Unknown }
public enum ValuationMoment { EndOfDay, BeforeFlow, AfterFlow }
public enum ReturnMethod { Exact, EndOfDayReconstructed }
public sealed record InvestmentAccount(string Id,string Broker,string Alias,string BaseCurrency,DateOnly TrustedFrom,bool Included=true,bool Margin=false,bool BaseCurrencyConfirmed=true);
public sealed record InvestmentPortfolio(string Id,string Name,string[] AccountIds,string Currency);
public sealed record PerformanceSource(string DocumentId,string RowIdentity);
public sealed record InvestmentEvent(string Id,string AccountId,DateTimeOffset Time,DateOnly Date,InvestmentEventKind Kind,
    decimal Amount,string Currency,PerformanceSource Source,string Security="",string Market="",decimal Quantity=0,
    decimal Fees=0,decimal Withholding=0,string Reference="",bool Confirmed=false,decimal SplitRatio=0,
    PerformanceQuality Quality=PerformanceQuality.Estimated,bool TimeVerified=false);
// 一个快照包含原币组成；证券市值缺失时仅能由显式价格和持仓数量重建。
public sealed record ValuationComponent(string Kind,string Currency,decimal? Amount,string Security="",string Market="",decimal Quantity=0,decimal? Cost=null);
public sealed record ValuationSnapshot(string Id,string AccountId,DateTimeOffset AsOf,DateOnly Date,ValuationMoment Moment,
    ValuationComponent[] Components,PerformanceSource Source,PerformanceQuality Quality=PerformanceQuality.Estimated,
    decimal? StatementNav=null,string StatementCurrency="",bool Reviewed=false,bool TimeVerified=false);
public sealed record SecurityPrice(string Id,string Security,string Market,string Currency,DateOnly Date,decimal Price,
    PerformanceSource Source,PerformanceQuality Quality=PerformanceQuality.Estimated,bool Adjusted=false);
public sealed record PerformanceFx(string Id,string From,string To,DateOnly Date,decimal Rate,PerformanceSource Source,
    PerformanceQuality Quality=PerformanceQuality.Estimated);
public sealed record PerformanceCoverage(string AccountId,DateOnly From,DateOnly To,bool ExternalFlowsComplete,
    PerformanceSource Source,decimal? ExplainedProfit=null,string Currency="",bool Reviewed=false,bool IncomeComplete=false,bool CostsComplete=false);
// 容差必须冻结币种，切换报告币种后不能重新解释同一个数字。
public sealed record TransferLink(string Id,string OutEventId,string InEventId,bool Confirmed,decimal DifferenceTolerance=0,string Reason="",string ToleranceCurrency="CNY");
public sealed record ClassificationRevision(string Id,string EventId,InvestmentEventKind Kind,bool Confirmed,string Reason,DateTimeOffset CreatedAt);
public sealed record PerformanceData
{
    public int Version {get;init;}=1;
    public InvestmentAccount[] Accounts {get;init;}=[];
    public InvestmentEvent[] Events {get;init;}=[];
    public ValuationSnapshot[] Valuations {get;init;}=[];
    public SecurityPrice[] Prices {get;init;}=[];
    public PerformanceFx[] Fx {get;init;}=[];
    public PerformanceCoverage[] Coverage {get;init;}=[];
    public TransferLink[] Transfers {get;init;}=[];
    public ClassificationRevision[] Revisions {get;init;}=[];
}
public sealed record PerformanceRequest(string[] AccountIds,DateOnly From,DateOnly To,string Currency="CNY",
    ReturnMethod Method=ReturnMethod.Exact,int PriceFallbackDays=0,decimal AbsoluteTolerance=0.01m,
    decimal RelativeTolerance=0.000001m,decimal CurrencyUnit=0.01m,bool SinceInception=false);
public sealed record PerformanceMetric(string Id,string Name,decimal? Value,string Unit,PerformanceQuality Quality,string Explanation,bool Annualized=false);
public sealed record PerformanceIssue(string Code,string AccountAlias,string SourceDocument,string SourceRow,string Explanation,string Action,bool BlocksReturns=true,string EventId="");
public sealed record PerformanceFlow(string EventId,string AccountAlias,DateOnly Date,DateTimeOffset Time,decimal Amount,bool Internal,string Explanation);
public sealed record PerformanceAccountResult(string AccountAlias,string Broker,decimal? OpeningNav,decimal? EndingNav,decimal? Cash,decimal? Liabilities,decimal? NetInvestment,decimal? Profit,string Currency);
public sealed record PerformanceReconciliation(string AccountAlias,DateOnly Date,decimal CalculatedNav,decimal StatementNav,decimal Difference,decimal Tolerance,bool Matched);
public sealed record PerformanceExposure(string AccountAlias,DateOnly Date,string Kind,string Currency,decimal? NativeAmount,decimal? ReportAmount,string SourceDocument,string SourceRow,string Security="",string Market="",decimal Quantity=0);
public sealed record PerformanceResult(string EngineVersion,PerformanceRequest Request,PerformanceMetric[] Metrics,PerformanceIssue[] Issues,
    PerformanceFlow[] Flows,PerformanceAccountResult[] Accounts,PerformanceReconciliation[] Reconciliations,DateOnly EffectiveFrom,
    string CostMethodVersion="InvestmentFIFO.v1")
{public PerformanceExposure[] Exposure {get;init;}=[];}
