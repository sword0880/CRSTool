using System.Xml.Linq;
using CRS.Domain;
using static CRS.Infrastructure.XmlFields;

namespace CRS.Infrastructure;

internal static class CashBalanceChecker
{
    // 逐份报告核对交易日余额，不能把重叠月份与全年报告相加。
    /// <summary>按原币验证期初加明细等于期末，换汇拆成两个币种腿及单独佣金。</summary>
    public static List<CashReconciliation> Check(XElement statement, string account, DateOnly start, DateOnly end, string file)
    {
        var movement = new Dictionary<string, decimal>();
        var reason = "";
        // 累加同一币种现金变动，不执行跨币种换算。
        void Add(string currency, decimal amount) => movement[currency] = movement.GetValueOrDefault(currency) + amount;
        foreach (var e in statement.Element("CashTransactions")?.Elements() ?? [])
            if (e.Name == "CashTransaction" && Optional(e, "levelOfDetail", "DETAIL") == "DETAIL") Add(Required(e, "currency"), Number(e, "amount"));
            else reason = "存在非明细现金记录。";
        foreach (var e in statement.Element("Trades")?.Elements("Trade").Where(e => Optional(e, "levelOfDetail") == "EXECUTION") ?? [])
        {
            var currency = Required(e, "currency");
            if (Optional(e, "origTradeID") is not ("" or "0") || Optional(e, "notes").Split([';', ',']).Any(n => n is "Ca" or "Co"))
                reason = "成交含更正或取消记录。";
            if (Optional(e, "assetCategory") is "STK" or "ETF")
            {
                if (Required(e, "ibCommissionCurrency") != currency) reason = "股票佣金币种与成交币种不同。";
                else Add(currency, Number(e, "netCash"));
            }
            else if (Optional(e, "assetCategory") == "CASH")
            {
                var pair = Required(e, "symbol").Split('.');
                if (pair.Length != 2 || pair[1] != currency || pair[0] == pair[1]) { reason = "无法识别换汇币种对。"; continue; }
                Add(pair[0], Number(e, "quantity")); Add(currency, Number(e, "proceeds"));
                Add(Required(e, "ibCommissionCurrency"), Number(e, "ibCommission"));
                if (Number(e, "taxes") != 0) reason = "换汇成交包含未支持税费。";
            }
            else reason = "现金核对含未支持资产。";
        }
        if (new[] { "CorporateActions", "Transfers", "TradeTransfers", "OptionEAE", "Adjustments", "UnsettledTransfers", "TransactionTaxes" }
            .Any(n => statement.Element(n)?.HasElements == true)) reason = "存在尚未处理的转仓或公司行动。";
        if (statement.Element("Trades") is null || statement.Element("CashTransactions") is null) reason = "成交或现金明细区段缺失。";
        var summaries = new Dictionary<string, XElement>();
        foreach (var e in statement.Element("CashReport")?.Elements().Where(e => Optional(e, "levelOfDetail") == "Currency") ?? [])
        {
            if (Optional(e, "accountId", account) != account) throw new CrsException("现金汇总账户不一致。");
            if (!summaries.TryAdd(Required(e, "currency"), e)) throw new CrsException("现金汇总币种重复。");
        }
        var currencies = movement.Keys.Concat(summaries.Keys).Distinct().ToList();
        if (currencies.Count == 0) currencies.Add("");
        var result = new List<CashReconciliation>();
        foreach (var currency in currencies)
        {
            var why = reason; decimal? beginning = null, reported = null;
            if (!summaries.TryGetValue(currency, out var s)) why = "缺少原币现金期初、期末余额。";
            else
            {
                if (Optional(s, "fromDate") == "" || Optional(s, "toDate") == "" || Date(Required(s, "fromDate")) != start || Date(Required(s, "toDate")) != end)
                    why = "现金汇总期间与活动报告不一致。";
                if (Optional(s, "startingCash") == "" || Optional(s, "endingCash") == "") why = "缺少交易日现金余额字段。";
                else { beginning = Number(s, "startingCash"); reported = Number(s, "endingCash"); }
            }
            var amount = movement.GetValueOrDefault(currency);
            decimal? calculated = beginning + amount, difference = calculated - reported;
            var status = why != "" ? "无法核对" : Math.Abs(difference!.Value) <= .02m ? "一致" : "存在差异";
            result.Add(new(account, currency, start, end, beginning, amount, calculated, reported, difference, status, why, file));
        }
        return result;
    }
}
