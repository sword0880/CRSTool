// 券商导入的统一中间结果，分别保留收入、交易、期初成本、现金对账、来源和问题，供后续校验及税务计算使用。
using System.Text.Json.Serialization;

namespace CRS.Application;

public sealed class ImportData
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public List<TaxImportProvenance>? ImportProvenance {get;set;}
    public string Broker { get; set; } = "IBKR";
    public List<AccountPeriodEvidence> AccountPeriods {get;}=[];
    public List<AnnualIncome> AnnualIncome { get; } = [];
    public List<FundMovement> FundMovements { get; } = [];
    public List<TradeAmountCheck> TradeAmountChecks { get; } = [];
    public List<SecurityMovement> SecurityMovements { get; } = [];
    public List<Position> BeginningPositions { get; } = [];
    public List<Trade> Trades { get; } = [];
    public List<Trade> ReportedSales { get; } = [];
    public List<CashEvent> Cash { get; } = [];
    public List<SourceReport> Sources { get; } = [];
    public List<Position> Positions { get; } = [];
    public List<CostLot> OpeningLots { get; } = [];
    public List<RoundingState> OpeningRounding { get; } = [];
    public List<Issue> Issues { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<CashReconciliation> CashChecks { get; } = [];
}
