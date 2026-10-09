using System.Globalization;
using System.Xml.Linq;
using CRS.Domain;
using static CRS.Infrastructure.XmlFields;

namespace CRS.Infrastructure;

public sealed class IbkrXmlParser
{
    private readonly Dictionary<(string Account, string Type, string Id), string> seen = [];
    private readonly Dictionary<(string Account, DateOnly End), Dictionary<SecurityKey, decimal>> snapshots = [];
    private readonly HashSet<(SecurityKey Key, string Id)> openingIds = [];
    private readonly ImportData data = new();
    private CancellationToken cancellation;

    /// <summary>导入活动报告与可选期初 LOT；同一账户原始 ID 去重，内容冲突拒绝。</summary>
    public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default)
    {
        if (data.Sources.Count != 0) throw new CrsException("请为每次导入创建新的解析器，避免复用旧状态。");
        cancellation = cancellationToken;
        var reports = files.ToList();
        if (reports.Count is 0 or > 36) throw new CrsException("请提供 1 至 36 份活动报告。");
        foreach (var path in reports) { cancellation.ThrowIfCancellationRequested(); Read(path, false); }
        if (opening is not null) Read(opening, true);
        return data;
    }
    /// <summary>校验文件类型和报告期间，并按账户分派成交、现金、持仓及未支持事件。</summary>
    private void Read(string path, bool opening)
    {
        var raw = ReadBytes(path); var doc = ReadXml(raw); var name = Path.GetFileName(path);
        if (doc.Root?.Name != "FlexQueryResponse" || Optional(doc.Root, "type", "AF") != "AF")
            throw new CrsException("请提供 Activity Flex XML，不接受请求回执或其他报告。");
        var statements = doc.Root.Element("FlexStatements")?.Elements("FlexStatement").ToList() ?? [];
        if (statements.Count == 0) throw new CrsException("XML 中没有 FlexStatement。");
        foreach (var s in statements)
        {
            cancellation.ThrowIfCancellationRequested();
            var account = Required(s, "accountId"); var start = Date(Required(s, "fromDate")); var end = Date(Required(s, "toDate"));
            if (start > end || start.Year < 2000 || end.Year > 2100) throw new CrsException("报告期间无效。");
            var positions = s.Element("OpenPositions");
            var hasPositions = positions is not null && (!positions.HasElements || positions.Elements().Any(e => Optional(e, "levelOfDetail") == "SUMMARY"));
            data.Sources.Add(new(name, Hash(raw), account, start, end, s.Element("Trades") is not null,
                s.Element("CashTransactions") is not null, hasPositions, opening,
                opening ? "OpeningLot" : "AnnualActivity", "IBKRActivityFlex"));
            if (opening) { ReadOpening(s, account, end, name); continue; }
            foreach (var field in new[] { "Trades", "CashTransactions" })
                if (s.Element(field) is null) data.Issues.Add(new("MISSING_SECTION", $"报告缺少 {field}。", account, File: name));
            ReadTrades(s, account, start, end, name);
            ReadCash(s, account, start, end, name);
            ReadPositions(s, account, end, name);
            var costSections = new[] { "CorporateActions", "Transfers", "TradeTransfers", "OptionEAE", "UnsettledTransfers",
                "StockGrantActivities", "TransactionTaxes", "Adjustments", "UnbookedTrades" };
            foreach (var section in costSections)
                foreach (var e in s.Element(section)?.Elements() ?? [])
                {
                    CheckAccount(e, account);
                    data.Issues.Add(new("UNSUPPORTED_TRANSFER", $"{section} 未支持，相关成本需复核。", account,
                        Optional(e, "symbol"), Optional(e, "currency"), EventDate(e), name,
                        Optional(e, "transactionID"), Optional(e, "conid"), true));
                }
            foreach (var section in new[] { "FxTransactions", "FxPositions", "CFDCharges", "SLBActivities", "SLBFees" })
                if (s.Element(section)?.HasElements == true)
                    data.Issues.Add(new("UNSUPPORTED_SECTION", $"{section} 尚未纳入测算。", account, File: name));
            data.CashChecks.AddRange(CashBalanceChecker.Check(s, account, start, end, name));
            CheckCashTotals(s, account, end, name);
        }
    }
    /// <summary>读取执行级成交，保留券商原始金额；边界异常进入复核并阻断相关成本链。</summary>
    private void ReadTrades(XElement s, string account, DateOnly start, DateOnly end, string name)
    {
        int row = 0, executions = 0, ignored = 0;
        foreach (var e in s.Element("Trades")?.Elements() ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            row++; var level = Optional(e, "levelOfDetail");
            if (e.Name != "Trade" || level != "EXECUTION")
            {
                if (new[] { "Order", "SymbolSummary", "AssetSummary", "Lot", "ClosedLot", "WashSale" }.Contains(e.Name.LocalName)
                    || new[] { "ORDER", "SYMBOL_SUMMARY", "ASSET_SUMMARY", "CLOSED_LOT", "WASH_SALE" }.Contains(level)) { ignored++; continue; }
                data.Issues.Add(new("UNKNOWN_TRADE_LEVEL", "只计算 EXECUTION 成交明细。", account, File: name)); continue;
            }
            executions++; CheckAccount(e, account);
            var id = Required(e, "tradeID");
            var rawTime = Optional(e, "dateTime", RequiredFallback(e));
            if (rawTime.Length is 8 or 10) throw new CrsException("成交时间须包含时分秒，不能仅有日期。");
            var time = Time(rawTime);
            var day = DateOnly.FromDateTime(time.Value.Date); CheckPeriod(day, start, end);
            if (Optional(e, "tradeDate") is { Length: > 0 } td && Date(td) != day) throw new CrsException("tradeDate 与 dateTime 不一致。");
            var canonical = Canonical(e, ["quantity", "tradePrice", "ibCommission", "taxes", "multiplier", "proceeds", "netCash", "fifoPnlRealized"],
                ["assetCategory", "conid", "symbol", "currency", "buySell", "ibCommissionCurrency", "openCloseIndicator", "transactionType", "origTradeID", "notes"], time.Value.ToString("O") + time.HasOffset);
            if (!Dedup(account, "Trade", id, canonical)) continue;
            var key = new SecurityKey(account, Optional(e, "conid"), Required(e, "currency"));
            var asset = Required(e, "assetCategory"); var side = Required(e, "buySell");
            var qty = Number(e, "quantity"); var price = Number(e, "tradePrice"); var commission = Number(e, "ibCommission");
            var pnl = Optional(e, "fifoPnlRealized") == "" ? (decimal?)null : Number(e, "fifoPnlRealized");
            if (asset is not ("STK" or "ETF"))
            {
                data.Trades.Add(new(key, Optional(e, "symbol"), time.Value, time.HasOffset, side, Math.Abs(qty), price,
                    Math.Abs(Number(e, "proceeds")), -commission, Number(e, "netCash"), id, name, row, asset, pnl));
                data.Issues.Add(new("UNSUPPORTED_ASSET", "资产类别未纳入股票 FIFO。", account, Optional(e, "symbol"), key.Currency, day, name, id, key.Instrument)); continue;
            }
            if (side is not ("BUY" or "SELL") || qty == 0 || price <= 0 || (side == "BUY") != (qty > 0)) throw new CrsException("买卖方向、数量符号或价格无效。");
            var unsupported = Optional(e, "origTradeID") is not ("" or "0") || Optional(e, "transactionType", "ExchTrade") != "ExchTrade"
                || Optional(e, "notes").Split([';', ',']).Any(n => n is "Ca" or "Co")
                || Optional(e, "openCloseIndicator") != (side == "BUY" ? "O" : "C")
                || Number(e, "multiplier") != 1 || Number(e, "taxes") != 0 || commission > 0 || Required(e, "ibCommissionCurrency") != key.Currency;
            var gross = Number(e, "proceeds"); var net = Number(e, "netCash");
            // 即使原始成交不能参与 FIFO，也保留券商卖出收益用于显示未核对原因。
            if (side == "SELL") data.ReportedSales.Add(new(key, Required(e, "symbol"), time.Value, time.HasOffset, side, Math.Abs(qty), price,
                Math.Abs(gross), -commission, net, id, name, row, asset, pnl));
            var proceedsMismatch = Math.Abs(gross + qty * price) > .02m;
            var netCashMismatch = Math.Abs(net - gross - commission - Number(e, "taxes")) > .02m;
            if (proceedsMismatch || netCashMismatch)
                data.Issues.Add(new("BROKER_AMOUNT_MISMATCH", "成交金额或变动金额与数量、价格、费用无法闭合，禁止静默按本地公式替代。",
                    account, Optional(e, "symbol"), key.Currency, day, name, id, key.Instrument, true));
            unsupported |= proceedsMismatch || netCashMismatch;
            key = key with { Instrument = Required(e, "conid") };
            if (unsupported)
            {
                data.Issues.Add(new("UNSUPPORTED_TRANSFER", "更正、开平仓、金额或费用边界需复核，相关库存不可信。", account,
                    Optional(e, "symbol"), key.Currency, day, name, id, key.Instrument, true)); continue;
            }
            data.Trades.Add(new(key, Required(e, "symbol"), time.Value, time.HasOffset, side, Math.Abs(qty), price,
                Math.Abs(gross), -commission, net, id, name, row, asset, pnl));
        }
        if (ignored > 0) data.Warnings.Add($"忽略 {ignored} 条订单、汇总或平仓批次。");
        if (ignored > 0 && executions == 0) data.Issues.Add(new("MISSING_EXECUTIONS", "Trades 只有汇总，请导出 Executions。", account, File: name));
    }
    /// <summary>缺少组合时间字段时，使用必填的交易日期与时分秒组合。</summary>
    private static string RequiredFallback(XElement e) => Optional(e, "dateTime") != "" ? "" : Required(e, "tradeDate") + ";" + Required(e, "tradeTime");
    /// <summary>保存现金原始事件并标记未支持类型，禁止把未知费用自动抵扣。</summary>
    private void ReadCash(XElement s, string account, DateOnly start, DateOnly end, string name)
    {
        foreach (var e in s.Element("CashTransactions")?.Elements() ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            CheckAccount(e, account);
            if (e.Name != "CashTransaction" || Optional(e, "levelOfDetail", "DETAIL") != "DETAIL")
            { data.Issues.Add(new("UNKNOWN_CASH_LEVEL", "现金记录必须为 DETAIL。", account, File: name)); continue; }
            var id = Required(e, "transactionID"); var stamp = Time(Required(e, "dateTime")).Value;
            var day = DateOnly.FromDateTime(stamp.Date); CheckPeriod(day, start, end);
            var canonical = Canonical(e, ["amount"], ["type", "currency", "conid", "symbol"], stamp.ToString("O"));
            if (!Dedup(account, "Cash", id, canonical)) continue;
            var kind = Required(e, "type"); var currency = Required(e, "currency"); var amount = Number(e, "amount");
            data.Cash.Add(new(account, day, kind, currency, amount, id, name));
            if (!new[] { "Dividends", "Broker Interest Received", "Bond Interest Received", "Withholding Tax", "Deposits/Withdrawals", "Deposits & Withdrawals" }.Contains(kind))
                data.Issues.Add(new("UNSUPPORTED_CASH", $"现金类型 {kind} 未自动计税或抵扣。", account, Optional(e, "symbol"), currency, day, name, id));
        }
    }
    /// <summary>读取期末 SUMMARY 数量，检查同账户同日完整快照是否冲突。</summary>
    private void ReadPositions(XElement s, string account, DateOnly end, string name)
    {
        var section = s.Element("OpenPositions"); if (section is null) return;
        var map = new Dictionary<SecurityKey, decimal>();
        foreach (var e in section.Elements().Where(e => Optional(e, "levelOfDetail") == "SUMMARY"))
        {
            cancellation.ThrowIfCancellationRequested();
            CheckAccount(e, account);
            if (Optional(e, "reportDate") is { Length: > 0 } rd && Date(rd) != end) throw new CrsException("持仓日期与报告截止日不一致。");
            var key = new SecurityKey(account, Required(e, "conid"), Required(e, "currency")); var qty = Number(e, "position");
            if (Optional(e, "assetCategory") is not ("STK" or "ETF") || Required(e, "side").ToUpperInvariant() != "LONG" || qty < 0)
            { data.Issues.Add(new("UNSUPPORTED_POSITION", "期末持仓类别或方向未支持。", account, File: name)); continue; }
            if (!map.TryAdd(key, qty)) throw new CrsException("期末持仓同一证券重复。");
            var p = new Position(key, Required(e, "symbol"), end, qty);
            if (!data.Positions.Contains(p)) data.Positions.Add(p);
        }
        if (section.HasElements && !section.Elements().Any(e => Optional(e, "levelOfDetail") == "SUMMARY"))
        { data.Issues.Add(new("MISSING_POSITION_SUMMARY", "期末数量核对需要 SUMMARY。", account, File: name)); return; }
        if (snapshots.TryGetValue((account, end), out var old) && (old.Count != map.Count || old.Any(p => map.GetValueOrDefault(p.Key, -1) != p.Value)))
            throw new CrsException("同账户同日的完整持仓快照冲突。");
        snapshots[(account, end)] = map;
    }
    /// <summary>只接受原始 LOT 成本批次，并在同时提供 SUMMARY 时核对数量和成本合计。</summary>
    private void ReadOpening(XElement s, string account, DateOnly end, string name)
    {
        var positions = s.Element("OpenPositions") ?? throw new CrsException("期初成本文件缺少 OpenPositions。");
        if (positions.Elements().Any(e => Optional(e, "levelOfDetail") is not ("LOT" or "SUMMARY"))) throw new CrsException("期初持仓包含未知明细级别。");
        if (positions.HasElements && !positions.Elements().Any(e => Optional(e, "levelOfDetail") == "LOT"))
            throw new CrsException("期初成本需 LOT 批次，不能使用 SUMMARY 平均成本。");
        var totals = new Dictionary<SecurityKey, (decimal Qty, decimal Cost)>();
        foreach (var e in positions.Elements().Where(e => Optional(e, "levelOfDetail") == "LOT"))
        {
            cancellation.ThrowIfCancellationRequested();
            CheckAccount(e, account);
            if (Optional(e, "reportDate") is { Length: > 0 } rd && Date(rd) != end) throw new CrsException("期初批次报告日期不一致。");
            var key = new SecurityKey(account, Required(e, "conid"), Required(e, "currency")); var qty = Number(e, "position"); var cost = Number(e, "costBasisMoney");
            var time = Time(Required(e, "openDateTime")); var id = Required(e, "originatingTransactionID");
            if (Optional(e, "assetCategory") is not ("STK" or "ETF") || Required(e, "side").ToUpperInvariant() != "LONG"
                || Number(e, "multiplier") != 1 || qty <= 0 || cost < 0 || DateOnly.FromDateTime(time.Value.Date) > end)
                throw new CrsException("期初批次数量、成本或买入时间无效。");
            if (!openingIds.Add((key, id))) throw new CrsException("期初批次 ID 重复。");
            data.OpeningLots.Add(new() { Key = key, Symbol = Required(e, "symbol"), Quantity = qty, Cost = cost,
                CostBasisMode = CostBasisMode.TotalCostIncludesFees,
                BuyTime = time.Value, HasOffset = time.HasOffset, RecordId = id, SourceFile = name });
            var old = totals.GetValueOrDefault(key); totals[key] = (old.Qty + qty, old.Cost + cost);
        }
        foreach (var e in positions.Elements().Where(e => Optional(e, "levelOfDetail") == "SUMMARY"))
        {
            cancellation.ThrowIfCancellationRequested();
            CheckAccount(e, account);
            var key = new SecurityKey(account, Required(e, "conid"), Required(e, "currency")); var t = totals.GetValueOrDefault(key);
            if (t.Qty != Number(e, "position") || Math.Abs(t.Cost - Number(e, "costBasisMoney")) > .02m) throw new CrsException("期初 LOT 与 SUMMARY 合计不一致。");
        }
    }
    /// <summary>核对股息、利息与预扣税明细和 CashReport 原币汇总的差异。</summary>
    private void CheckCashTotals(XElement s, string account, DateOnly end, string name)
    {
        if (s.Element("CashReport") is null || s.Element("CashTransactions") is null) return;
        var fields = new Dictionary<string, string[]> { ["dividends"] = ["Dividends"], ["brokerInterest"] = ["Broker Interest Received", "Broker Interest Paid"], ["withholdingTax"] = ["Withholding Tax"] };
        var summaries = s.Element("CashReport")!.Elements().Where(e => Optional(e, "levelOfDetail") == "Currency").ToDictionary(e => Required(e, "currency"));
        var rawCash = s.Element("CashTransactions")!.Elements("CashTransaction").Where(e => Optional(e, "levelOfDetail", "DETAIL") == "DETAIL").ToList();
        foreach (var currency in rawCash.Select(e => Required(e, "currency")).Concat(summaries.Keys).Distinct())
            foreach (var (field, kinds) in fields)
            {
                var detailed = rawCash.Where(e => Required(e, "currency") == currency && kinds.Contains(Required(e, "type"))).Sum(e => Number(e, "amount"));
                var present = summaries.TryGetValue(currency, out var summary) && Optional(summary, field) != "";
                if ((!present && detailed != 0) || present && Math.Abs(Number(summary!, field) - detailed) > .02m)
                    data.Issues.Add(new("CASH_REPORT_MISMATCH", $"{field} 原币汇总缺失或与明细不一致。", account, Currency: currency, Date: end, File: name));
            }
    }
    /// <summary>按账户、类型和 ID 去重；同 ID 不同语义内容直接拒绝。</summary>
    private bool Dedup(string account, string kind, string id, string canonical)
    {
        var key = (account, kind, id);
        if (seen.TryGetValue(key, out var old))
        { if (old != canonical) throw new CrsException("同一账户记录 ID 内容冲突，请使用一致版本。"); return false; }
        seen[key] = canonical; return true;
    }
    /// <summary>归一数字精度后生成内容指纹，避免格式差异导致错误的重复判断。</summary>
    private static string Canonical(XElement e, string[] numbers, string[] texts, string time) => HashJson(new {
        time, numbers = numbers.Select(f => Optional(e, f) == "" ? "" : Number(e, f).ToString("G29", CultureInfo.InvariantCulture)),
        texts = texts.Select(f => Optional(e, f)) });
    /// <summary>验证明细账户归属，拒绝未支持的 Model 分区。</summary>
    private static void CheckAccount(XElement e, string account)
    {
        if (Optional(e, "accountId", account) != account || Optional(e, "model") != "") throw new CrsException("记录账户不一致或使用了未支持的 Model 分区。");
    }
    /// <summary>要求记录日期位于声明的报告区间内。</summary>
    private static void CheckPeriod(DateOnly day, DateOnly start, DateOnly end)
    { if (day < start || day > end) throw new CrsException("记录日期超出报告期间。"); }
    /// <summary>尝试提取未支持事件日期；无法识别时保留未知日期供保守复核。</summary>
    private static DateOnly? EventDate(XElement e)
    {
        foreach (var field in new[] { "tradeDate", "dateTime", "date", "reportDate" })
            if (Optional(e, field) is { Length: >= 8 } raw) { try { return Date(raw[..Math.Min(raw.Length, raw.Contains('-') ? 10 : 8)]); } catch (CrsException) { } }
        return null;
    }
}
