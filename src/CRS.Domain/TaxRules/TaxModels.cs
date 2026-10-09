using System.Text.Json.Serialization;

namespace CRS.Domain;

public sealed record Income(string Account, int Year, string Currency, decimal Dividend, decimal Interest);
/// <summary>保留原币净预扣税事实；正数为缴税，负数为税款退回，不预先施加抵免上限。</summary>
public sealed record ForeignTaxPayment(string Account, int Year, string Currency, decimal Amount);

/// <summary>年度税额的可聚合事实，保存收入与未限额的税款，防止从已算税额反推输入。</summary>
public sealed record TaxCalculationInputs(int Version, string RuleVersion, List<Income> Income,
    List<ForeignTaxPayment> ForeignTax, string Broker = "IBKR");
public sealed record TaxSummary(decimal DividendCny, decimal InterestCny, decimal GainCny,
    decimal DividendInterestTax, decimal GainTax, decimal ForeignCredit, decimal SupplementTax,
    decimal? EstimatedTopUpCny = null);
