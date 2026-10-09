using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using System.Security.Cryptography;
using System.Text.Json;

internal static class AnnualCalculationChecks
{
    public static void Run()
    {
        var gap = new ImportData();
        gap.Sources.Add(Report("A", new(2025, 1, 2)));
        gap.Issues.Add(new("FUTURE_ONLY", "下一年度问题", Date: new(2026, 1, 1)));
        var incomplete = Calculate(gap);
        Assert(incomplete.Issues.Count(i => i.Code == "INCOMPLETE_PERIOD") == 2,
            "年度开始缺一天必须同时阻断成交和现金完整性。");
        Assert(incomplete.Issues.All(i => i.Code != "FUTURE_ONLY"), "下一年度问题混入当前结果。");

        var carried = Data("A");
        var key = new SecurityKey("A", "STOCK", "USD");
        var lot = new CostLot { Key = key, Symbol = "STOCK", BuyTime = new(2024, 12, 1, 0, 0, 0, TimeSpan.Zero),
            RecordId = "OPEN", SourceFile = "synthetic", HasOffset = true, Quantity = 1m, Cost = 10m };
        var carry = new CarryDocument(1, 2024, "synthetic", ["A"], [lot], []);
        carried.Trades.Add(Trade(key, "SELL", "SELL", 20m));
        var consumed = Calculate(carried, carry);
        Assert(lot.Quantity == 1m && lot.Cost == 10m && consumed.Matches.Single().Gain == 10m,
            "年度计算修改了调用方的结转余额或收益。");
        var wrongBroker = carry with { Lots = [new CostLot { Key = key with { Broker = "FUTU" },
            Symbol = lot.Symbol, BuyTime = lot.BuyTime, RecordId = lot.RecordId, SourceFile = lot.SourceFile,
            Quantity = lot.Quantity, Cost = lot.Cost }] };
        try { Calculate(Data("A"), wrongBroker); throw new Exception("跨券商结转被接受。"); }
        catch (CrsException ex) when (ex.Message.Contains("券商")) { }

        var sales = Data("A", "BUY", "C");
        foreach (var account in new[] { "A", "B" })
        {
            var saleKey = key with { Account = account };
            sales.Trades.Add(Trade(saleKey, "BUY", "BUY", 10m));
            var sell = Trade(saleKey, "SHARED-ID", "SELL", account == "A" ? 20m : 30m)
                with { ReportedPnl = account == "A" ? 10m : 20m };
            sales.Trades.Add(sell); sales.ReportedSales.Add(sell);
        }
        sales.ReportedSales.Add(Trade(key with { Account = "C" }, "SHARED-ID", "SELL", 40m) with { ReportedPnl = 30m });
        var reconciled = Calculate(sales);
        Assert(reconciled.PnlChecks.Single(p => p.Account == "A").Calculated == 10m
            && reconciled.PnlChecks.Single(p => p.Account == "B").Calculated == 20m
            && reconciled.PnlChecks.Single(p => p.Account == "C").Calculated is null,
            "同一卖出编号跨账户串用了匹配批次，或缺失成本变成零。");
        Assert(reconciled.PnlChecks.Take(2).All(p => p.Status == "一致")
            && reconciled.PnlChecks.Last().Status == "成本不完整", "对账状态错误。");

        using var cancellation = new CancellationTokenSource();
        var importer = new SyntheticImporter(Data("A"), cancellation.Cancel);
        var service = new CalculationService(new Rates(), importer, new SyntheticCarryValidator(), new SyntheticEvidence());
        try { service.Calculate([], 2025, true, true, cancellation: cancellation.Token);
            throw new Exception("导入后取消仍继续计算。"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("年度计算验证通过：覆盖缺口、结转隔离、同编号账户对账和导入后取消（4 项）。");
    }

    private static CalculationResult Calculate(ImportData data, CarryDocument? carry = null) =>
        new CalculationService(new Rates(), new SyntheticImporter(data), new SyntheticCarryValidator(), new SyntheticEvidence())
            .Calculate([], 2025, carry is null, true, carry: carry);

    private static ImportData Data(params string[] accounts)
    {
        var data = new ImportData();
        foreach (var account in accounts) data.Sources.Add(Report(account, new(2025, 1, 1)));
        return data;
    }

    private static SourceReport Report(string account, DateOnly begin) =>
        new("synthetic", "synthetic", account, begin, new(2025, 12, 31), true, true, true);

    private static Trade Trade(SecurityKey key, string id, string side, decimal price) =>
        new(key, "STOCK", new(2025, side == "BUY" ? 1 : 2, 1, 12, 0, 0, TimeSpan.Zero), true,
            side, 1m, price, price, 0m, side == "BUY" ? -price : price, id, "synthetic", 1, "STK", null);

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class SyntheticImporter(ImportData data, Action? afterParse = null) : IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default)
        { afterParse?.Invoke(); return data; }
    }

    private sealed class SyntheticCarryValidator : ICarryValidator
    {
        public void Validate(CarryDocument document, int year, string[] accounts) { }
    }

    private sealed class SyntheticEvidence : ICalculationEvidence
    {
        public string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public string HashJson(object value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        public string FileHash(string path) => "synthetic";
        public string InfrastructureAssemblyHash => "synthetic";
    }
}
