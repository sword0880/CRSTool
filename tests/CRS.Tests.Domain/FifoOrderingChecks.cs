using CRS.Domain;

internal static class FifoOrderingChecks
{
    /// <summary>乱序输入、跨文件同秒和期初同秒分别验证稳定排序及歧义隔离。</summary>
    public static void Run()
    {
        var key = new SecurityKey("A", "STOCK", "USD");
        var time = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Trade Buy(string id, int row, decimal cost) => new(key, "STOCK", time, true, "BUY", 1, cost, cost, 0, -cost, id, "same.xml", row, "STK", null);
        var first = Buy("Z", 1, 100); var second = Buy("A", 2, 200);
        var sale = first with { Time = time.AddDays(1), Id = "SELL", Row = 3, Side = "SELL", Price = 300, Gross = 300, Net = 300 };
        var forward = new FifoEngine().Calculate([first, second, sale], [], [], [], 2025, (_, _) => 1);
        var shuffled = new FifoEngine().Calculate([sale, second, first], [], [], [], 2025, (_, _) => 1);
        if (!forward.SequenceEqual(shuffled) || forward.Single().BuyId != "Z" || forward.Single().Gain != 200)
            throw new Exception("同文件行序没有稳定优先于编号及输入选择顺序。");

        var otherKey = key with { Instrument = "OTHER" };
        var engine = new FifoEngine();
        var matches = engine.Calculate([first, second with { File = "other.xml", Row = 1 }, sale,
            first with { Key = otherKey }, sale with { Key = otherKey }], [], [], [], 2025, (_, _) => 1);
        if (!engine.Issues.Any(i => i.Code == "AMBIGUOUS_FIFO_ORDER" && i.AffectsCost)
            || matches.Any(m => m.Key == key) || matches.Count != 1 || engine.EndingLots.Any(l => l.Key == key))
            throw new Exception("跨文件同秒顺序被当成可信成本，或阻断了独立证券。");
        var reverse = new FifoEngine();
        reverse.Calculate([sale, second with { File = "other.xml", Row = 1 }, first], [], [], [], 2025, (_, _) => 1);
        if (!engine.Issues.Where(i => i.Instrument == key.Instrument).SequenceEqual(reverse.Issues))
            throw new Exception("歧义问题清单随输入顺序改变。");
        var sameRow = new FifoEngine();
        sameRow.Calculate([first,second with { Row=1 },sale],[],[],[],2025,(_,_)=>1);
        if (!sameRow.Uncertain.Contains(key)) throw new Exception("同文件相同行序没有进入歧义门槛。");

        CostLot Lot(string id, decimal cost) => new() { Key = key, Symbol = "STOCK", BuyTime = time.AddYears(-1), HasOffset = true,
            RecordId = id, SourceFile = "opening", Quantity = 1, Cost = cost };
        var openingEngine = new FifoEngine();
        openingEngine.Calculate([sale], [Lot("A", 100), Lot("B", 200)], [], [], 2025, (_, _) => 1);
        if (!openingEngine.Uncertain.Contains(key) || !openingEngine.Issues.Any(i => i.Code == "AMBIGUOUS_FIFO_ORDER"))
            throw new Exception("无法证明先后的期初批次未进入复核门槛。");
        Console.WriteLine("FIFO 顺序验证通过：乱序稳定、跨文件歧义隔离、期初同秒复核（3 项）。");
    }
}
