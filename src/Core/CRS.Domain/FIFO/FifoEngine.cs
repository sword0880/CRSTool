namespace CRS.Domain;

public sealed record FifoAccountTransfer(SecurityKey From, SecurityKey To, DateOnly Date, decimal Quantity, string File);

public sealed class FifoEngine
{
    // 显式使用远离零的中点舍入，与 Python ROUND_HALF_UP 保持一致。
    /// <summary>按人民币分的精度四舍五入，中点远离零，与原版金额规则一致。</summary>
    public static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    public List<Issue> Issues { get; } = [];
    public HashSet<SecurityKey> Uncertain { get; } = [];
    public List<CostLot> EndingLots { get; } = [];
    public List<RoundingState> EndingRounding { get; } = [];
    public Dictionary<SecurityKey, decimal> EndingQuantities { get; } = [];

    /// <summary>隔离账户、证券和币种逐批匹配，保留原始金额及跨年舍入余额；不完整成本阻断相关库存。</summary>
    public List<Match> Calculate(IEnumerable<Trade> input, IEnumerable<CostLot> opening,
        IEnumerable<RoundingState> rounding, IEnumerable<Issue> events, int year, Func<int, string, decimal> rate,
        CancellationToken cancellation = default, IReadOnlyList<FifoAccountTransfer>? accountTransfers = null)
    {
        Issues.Clear(); Uncertain.Clear(); EndingLots.Clear(); EndingRounding.Clear(); EndingQuantities.Clear();
        var trades = FifoOrdering.Trades(input.Where(t => (t.Asset is "STK" or "ETF" ||
            t is { Asset: "FUND", Key.Broker: "FUTU" }) && t.Time.Year <= year)).ToList();
        var openingLots = FifoOrdering.Lots(opening).ToList();
        var transfers = (accountTransfers ?? []).OrderBy(t => t.Date).ToArray();
        // 同时刻跨文件或相同行序无法证明先后，不能靠文件选择顺序发布可信成本。
        foreach (var group in trades.GroupBy(t => (t.Key, t.Time)))
            if (group.Count() > 1 && (group.Select(t => t.File).Distinct(StringComparer.Ordinal).Count() > 1
                || group.GroupBy(t => t.Row).Any(rows => rows.Count() > 1)))
                Ambiguous(group.Key.Key, group.Key.Time, FifoOrdering.Trades(group).First().Symbol);
        foreach (var group in openingLots.GroupBy(l => (l.Key, l.BuyTime)))
            if (group.Count() > 1) Ambiguous(group.Key.Key, group.Key.BuyTime, group.First().Symbol);
        var stocks = new Dictionary<SecurityKey, Queue<CostLot>>();
        // 成本账只维护一个可扣减的含费总成本；Fee 仅作为审计披露，不重复参与收益公式。
        var ledgers = new Dictionary<SecurityKey, (decimal TotalCost, decimal TotalCostAllocated, decimal Fee, decimal FeeAllocated)>();
        foreach (var r in rounding)
        {
            if (Math.Abs(r.CostDelta) > .005m || Math.Abs(r.FeeDelta) > .005m || ledgers.ContainsKey(r.Key))
                throw new CrsException("结转舍入余额重复或超出半分。");
            ledgers[r.Key] = (r.CostDelta, 0, r.FeeDelta, 0);
        }
        foreach (var lot in openingLots)
        {
            cancellation.ThrowIfCancellationRequested();
            if (lot.Quantity <= 0 || lot.Cost < 0 || lot.Fee < 0) throw new CrsException("期初批次数量或成本无效。");
            if (lot.CostBasisMode == CostBasisMode.Unknown)
            {
                Uncertain.Add(lot.Key);
                Issues.Add(new("MISSING_COST", "期初 LOT 未声明成本是否含费，不能形成完整收益。", lot.Key.Account,
                    lot.Symbol, lot.Key.Currency, DateOnly.FromDateTime(lot.BuyTime.Date), lot.SourceFile, lot.RecordId,
                    lot.Key.Instrument, true));
                continue;
            }
            if (trades.Any(t => t.Key == lot.Key && t.Time.Year < year)) throw new CrsException("期初批次与历史成交重叠。");
            Get(lot.Key).Enqueue(lot.Copy());
        }
        foreach (var group in trades.GroupBy(t => t.Key))
            if (group.Select(t => t.HasOffset).Concat(openingLots.Where(l => l.Key == group.Key).Select(l => l.HasOffset)).Distinct().Count() > 1)
                throw new CrsException("同账户证券混用带时区和无时区时间。");
        var keys = trades.Select(t => t.Key).Concat(openingLots.Select(l => l.Key))
            .Concat(transfers.SelectMany(t => new[] { t.From, t.To })).Distinct().ToArray();
        foreach (var e in events.Where(e => e.AffectsCost))
            foreach (var key in keys)
                if ((e.Account == "" || e.Account == key.Account) && (e.Instrument == "" || e.Instrument == key.Instrument)
                    && (e.Currency == "" || e.Currency == key.Currency)) Uncertain.Add(key);
        var matches = new List<Match>();
        var transferIndex = 0;
        foreach (var t in trades)
        {
            cancellation.ThrowIfCancellationRequested();
            var tradeDate = DateOnly.FromDateTime(t.Time.Date);
            while (transferIndex < transfers.Length && transfers[transferIndex].Date <= tradeDate)
                MoveAccountLots(transfers[transferIndex++]);
            if (t.Quantity <= 0 || t.Price <= 0 || t.Fee < 0 || t.Gross < 0) throw new CrsException("成交数量、价格或费用无效。");
            var queue = Get(t.Key);
            if (t.Side == "BUY")
            {
                queue.Enqueue(new CostLot { Key = t.Key, Symbol = t.Symbol, BuyTime = t.Time, HasOffset = t.HasOffset,
                    Quantity = t.Quantity, Cost = t.Gross, Fee = t.Fee, CostBasisMode = CostBasisMode.PrincipalPlusFees,
                    RecordId = t.Id, SourceFile = t.File });
                continue;
            }
            if (t.Side != "SELL") throw new CrsException("交易方向无效。");
            if (Uncertain.Contains(t.Key) || queue.Sum(l => l.Quantity) < t.Quantity)
            {
                Uncertain.Add(t.Key);
                Issues.Add(new("MISSING_COST", "此笔或此前成本不完整，不能使用后续买入恢复可信库存。", t.Key.Account,
                    t.Symbol, t.Key.Currency, DateOnly.FromDateTime(t.Time.Date), t.File, t.Id, t.Key.Instrument, true));
                continue;
            }
            decimal remaining = t.Quantity, revenueExact = 0, revenueAllocated = 0, sellFeeExact = 0, sellFeeAllocated = 0;
            decimal cnyExact = 0, cnyAllocated = 0;
            while (remaining > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var lot = queue.Peek(); var qty = Math.Min(remaining, lot.Quantity);
                var principal = qty == lot.Quantity ? lot.Cost : lot.Cost * qty / lot.Quantity;
                var fee = qty == lot.Quantity ? lot.Fee : decimal.Round(lot.Fee * qty / lot.Quantity, 8, MidpointRounding.AwayFromZero);
                var cost = qty == lot.Quantity ? lot.TotalCost : lot.TotalCost * qty / lot.Quantity;
                var ledger = ledgers.GetValueOrDefault(t.Key);
                ledger.TotalCost += cost; ledger.Fee += fee;
                var costAllocated = Money(ledger.TotalCost) - ledger.TotalCostAllocated;
                var feeAllocated = Money(ledger.Fee) - ledger.FeeAllocated;
                ledger.TotalCostAllocated += costAllocated; ledger.FeeAllocated += feeAllocated; ledgers[t.Key] = ledger;
                // 按整笔原始收入和费用累计分配，确保最后一批吸收余差。
                revenueExact += t.Gross * qty / t.Quantity; sellFeeExact += t.Fee * qty / t.Quantity;
                var revenue = Money(revenueExact) - revenueAllocated;
                var sellFee = Money(sellFeeExact) - sellFeeAllocated;
                revenueAllocated += revenue; sellFeeAllocated += sellFee;
                // 含费总成本已经包含买入费用，FeeAllocated 只用于披露，不能再次扣减。
                var gain = revenue - sellFee - costAllocated;
                if (t.Time.Year == year)
                {
                    cnyExact += gain * rate(year, t.Key.Currency);
                    var gainCny = Money(cnyExact) - cnyAllocated; cnyAllocated += gainCny;
                    matches.Add(new(t.Key, t.Symbol, DateOnly.FromDateTime(lot.BuyTime.Date), DateOnly.FromDateTime(t.Time.Date),
                        qty, costAllocated, revenue, feeAllocated, sellFee, gain, gainCny, lot.RecordId, t.Id, lot.SourceFile, t.File,
                        lot.CostBasisMode));
                }
                lot.Quantity -= qty; lot.Cost -= principal; lot.Fee -= fee; remaining -= qty;
                if (lot.Quantity == 0) queue.Dequeue();
            }
        }
        while (transferIndex < transfers.Length) MoveAccountLots(transfers[transferIndex++]);
        foreach (var (key, queue) in stocks)
        {
            EndingQuantities[key] = queue.Sum(l => l.Quantity);
            if (!Uncertain.Contains(key)) EndingLots.AddRange(queue.Select(l => l.Copy()));
        }
        EndingRounding.AddRange(ledgers.Where(p => !Uncertain.Contains(p.Key)).Select(p =>
            new RoundingState(p.Key, p.Value.TotalCost - p.Value.TotalCostAllocated, p.Value.Fee - p.Value.FeeAllocated)));
        return matches;

        void MoveAccountLots(FifoAccountTransfer transfer)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = Get(transfer.From);
            var target = Get(transfer.To);
            // 仅完整库存、无同证券目标库存时保留原始成本顺序和舍入余差。
            if (Uncertain.Contains(transfer.From) || Uncertain.Contains(transfer.To) ||
                source.Sum(l => l.Quantity) != transfer.Quantity || target.Count != 0 || ledgers.ContainsKey(transfer.To))
            {
                Uncertain.Add(transfer.From);
                Uncertain.Add(transfer.To);
                Issues.Add(new("FUTU_UPGRADE_COST_GAP", "券商升级转入转出已配对，但原账户成本库存不完整或目标账户存在历史库存。",
                    transfer.To.Account, Currency: transfer.To.Currency, Date: transfer.Date, File: transfer.File,
                    Instrument: transfer.To.Instrument, AffectsCost: true));
                return;
            }
            foreach (var lot in source)
                target.Enqueue(new CostLot
                {
                    Key = transfer.To, Symbol = lot.Symbol, BuyTime = lot.BuyTime, HasOffset = lot.HasOffset,
                    RecordId = lot.RecordId, SourceFile = lot.SourceFile, Quantity = lot.Quantity,
                    Cost = lot.Cost, Fee = lot.Fee, CostBasisMode = lot.CostBasisMode
                });
            source.Clear();
            if (ledgers.Remove(transfer.From, out var ledger)) ledgers.Add(transfer.To, ledger);
        }

        // 保留可定位的复核事项，并隔离受影响证券；其他证券仍可独立核算。
        void Ambiguous(SecurityKey key, DateTimeOffset time, string symbol)
        {
            Uncertain.Add(key);
            Issues.Add(new("AMBIGUOUS_FIFO_ORDER", "同一时刻的批次先后无法由来源行序证明，请核对原始成交顺序。",
                key.Account, symbol, key.Currency, DateOnly.FromDateTime(time.Date), Instrument: key.Instrument, AffectsCost: true));
        }

        // 获取当前证券库存队列；首次出现时创建独立库存。
        Queue<CostLot> Get(SecurityKey key)
        {
            if (!stocks.TryGetValue(key, out var q)) stocks[key] = q = new();
            return q;
        }
    }
}
