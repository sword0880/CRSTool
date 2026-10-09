using System.Text.Json.Serialization;

namespace CRS.Application;

public sealed class ImportData
{
    public string Broker { get; set; } = "IBKR";
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
