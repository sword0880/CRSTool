using System.Text;
using CRS.Domain.Performance;
namespace CRS.Infrastructure;

internal static class PerformanceBrokerMapping
{
    internal static string AccountId(string broker,string raw)=>broker+":"+PerformanceCodec.Hash(Encoding.UTF8.GetBytes(raw))[..24];
    internal static DateTimeOffset End(DateOnly day)=>new(day.ToDateTime(new(23,59,59)),TimeSpan.Zero);
    internal static InvestmentEventKind CashKind(string type,decimal amount)=>type.Trim().ToUpperInvariant() switch
    {
        "DEPOSITS/WITHDRAWALS" or "入金" or "出金" or "存入资金" or "提取资金"=>amount>=0?InvestmentEventKind.Deposit:InvestmentEventKind.Withdrawal,
        "DIVIDENDS" or "PAYMENT IN LIEU OF DIVIDENDS" or "现金股息" or "派息" or "股息"=>InvestmentEventKind.Dividend,
        "BROKER INTEREST RECEIVED" or "利息"=>InvestmentEventKind.Interest,
        "BROKER INTEREST PAID" or "融资利息"=>InvestmentEventKind.FinancingInterest,
        "WITHHOLDING TAX" or "预扣税"=>InvestmentEventKind.WithholdingTax,
        "OTHER FEES" or "费用" or "手续费"=>InvestmentEventKind.Fee,
        "INTERNAL TRANSFER" or "INTERNAL TRANSFERS" or "TRANSFER" or "转账"=>InvestmentEventKind.CashTransfer,
        "换汇" or "兑换"=>InvestmentEventKind.FxExchange,
        _=>InvestmentEventKind.Unknown
    };
}
