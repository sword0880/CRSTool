using System.Text.Json.Serialization;

namespace CRS.Domain;

// 稳定证券标识优先于展示代码，账户、券商、市场和币种共同参与库存隔离。
public readonly record struct SecurityKey(string Account, string Instrument, string Currency,
    string Broker = "IBKR", string Market = "");
public sealed record Trade(SecurityKey Key, string Symbol, DateTimeOffset Time, bool HasOffset, string Side,
    decimal Quantity, decimal Price, decimal Gross, decimal Fee, decimal Net, string Id, string File, int Row,
    string Asset, decimal? ReportedPnl);
public sealed record CashEvent(string Account, DateOnly Date, string Type, string Currency, decimal Amount, string Id, string File);
public sealed record SourceReport(string File, string Sha256, string Account, DateOnly Start, DateOnly End,
    bool HasTrades, bool HasCash, bool HasPositions, bool Opening = false,
    string Purpose = "AnnualActivity", string Authority = "ImportedFile");
public sealed record Position(SecurityKey Key, string Symbol, DateOnly Date, decimal Quantity);
public sealed record Issue(string Code, string Message, string Account = "", string Symbol = "", string Currency = "",
    DateOnly? Date = null, string File = "", string RecordId = "", string Instrument = "", bool AffectsCost = false);
public sealed record CashReconciliation(string Account, string Currency, DateOnly Start, DateOnly End,
    decimal? Starting, decimal Movement, decimal? CalculatedEnding, decimal? ReportedEnding, decimal? Difference,
    string Status, string Reason, string File);
public sealed record PnlReconciliation(string Account, string Symbol, string Currency, string TradeId,
    decimal? Calculated, decimal? Reported, decimal? Difference, string Status);
public sealed record Match(SecurityKey Key, string Symbol, DateOnly BuyDate, DateOnly SellDate, decimal Quantity,
    decimal Cost, decimal Revenue, decimal BuyFee, decimal SellFee, decimal Gain, decimal GainCny,
    string BuyId, string SellId, string BuyFile, string SellFile,
    CostBasisMode CostBasisMode = CostBasisMode.PrincipalPlusFees);
