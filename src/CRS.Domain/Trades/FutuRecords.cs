namespace CRS.Domain;

/// <summary>年度收入主表；现金派息明细仅作核对，不能再次计税。</summary>
public sealed record AnnualIncome(string Account, string AccountName, int Year, string Currency,
    decimal Dividend, decimal Interest, decimal OtherIncome, string File, string Sheet, int Row);
/// <summary>富途现金流水，包括入金、派息、扣税与其他变动，保留来源定位。</summary>
public sealed record FundMovement(string Account, string AccountName, DateOnly Date, string Currency,
    string Type, string Direction, decimal Amount, string Description, string Symbol, string File, string Sheet, int Row);
/// <summary>原始成交金额与数量价格、总费用之间的核对事实。</summary>
public sealed record TradeAmountCheck(string Account, string Symbol, string Currency, string TradeId,
    decimal ExpectedGross, decimal? ReportedGross, decimal ExpectedNet, decimal? ReportedNet,
    string Status, string File, string Sheet, int Row);
/// <summary>证券资产事件保留原始证券与数量，未知事件不补造零成本。</summary>
public sealed record SecurityMovement(SecurityKey Key, string Symbol, DateOnly Date, string Type,
    string Direction, decimal Quantity, string Description, string File, string Sheet, int Row);
