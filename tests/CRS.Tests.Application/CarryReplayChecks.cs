using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CarryReplayChecks
{
    /// <summary>结转复算必须恢复期初确认、成本余差和来源，不重复合入批次。</summary>
    public static void Run()
    {
        var key = new SecurityKey("A", "STOCK", "USD");
        var lot = new CostLot { Key = key, Symbol = "STOCK", BuyTime = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), HasOffset = true,
            RecordId = "OPEN", SourceFile = "prior.xml", Quantity = 3, Cost = 8.0003m, Fee = .0001m };
        var carry = new CarryDocument(1, 2024, "prior-run", ["A"], [lot], [new(key, .0048m, .0041m)]);
        var service = new CalculationService(new Rates(), new Importer(key), new Validator(), new Evidence(), timeProvider: new Clock());
        var original = service.Calculate(["report.xml"], 2025, false, true, carry: carry, saveCanonicalSnapshot: true);
        using (var canonical = JsonDocument.Parse(original.CanonicalInputJson))
            if (canonical.RootElement.GetProperty("openingLots").GetArrayLength() != 0
                || canonical.RootElement.GetProperty("carry").GetProperty("Lots")[0].GetProperty("Quantity").GetDecimal() != 3)
                throw new Exception("规范化快照混用了已合入期初和原始结转。");
        var replay = service.Replay(original);
        var secondReplay = service.Replay(replay);
        if (original.Summary != replay.Summary || !original.Matches.SequenceEqual(replay.Matches)
            || !original.EndingRounding.SequenceEqual(replay.EndingRounding)
            || JsonSerializer.Serialize(original.EndingLots) != JsonSerializer.Serialize(replay.EndingLots)
            || replay.Issues.Any(i => i.Code == "OPENING_UNCONFIRMED") || original.CarryEligible != replay.CarryEligible
            || replay.ParentSnapshotId != original.SnapshotId || secondReplay.ParentSnapshotId != replay.SnapshotId
            || lot.Quantity != 3 || original.EndingLots.Single().Quantity != 2 || secondReplay.EndingLots.Single().Quantity != 2)
            throw new Exception("结转复算金额、批次、余差、确认状态或父运行不一致。");

        // 即使输入摘要未变，也必须拒绝与已保存业务输出不一致的复算。
        var changed = JsonSerializer.SerializeToNode(original)!.AsObject();
        changed["Summary"]!["GainCny"] = original.Summary.GainCny + 1;
        Reject(() => service.Replay(changed.Deserialize<CalculationResult>()!), "不一致");
        var old = JsonSerializer.SerializeToNode(original)!.AsObject();
        var oldInput = JsonNode.Parse(original.CanonicalInputJson)!.AsObject(); oldInput["schema"] = "CRS.CanonicalInput.v1";
        old["CanonicalInputJson"] = oldInput.ToJsonString();
        old["CanonicalInputDigest"] = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(oldInput.ToJsonString())));
        Reject(() => service.Replay(old.Deserialize<CalculationResult>()!), "版本");
        // 相同行序以外的来源、现金和问题也必须完整排序，输入文件重排不能改变快照摘要。
        var forward = new CalculationService(new Rates(), new SnapshotImporter(false), new Validator(), new Evidence(), timeProvider: new Clock())
            .Calculate(["b.xml","a.xml"],2025,true,true,saveCanonicalSnapshot:true);
        var reverse = new CalculationService(new Rates(), new SnapshotImporter(true), new Validator(), new Evidence(), timeProvider: new Clock())
            .Calculate(["a.xml","b.xml"],2025,true,true,saveCanonicalSnapshot:true);
        if (forward.CanonicalInputDigest != reverse.CanonicalInputDigest || !forward.Matches.SequenceEqual(reverse.Matches)
            || forward.Summary != reverse.Summary) throw new Exception("重排输入导致 FIFO 或规范化快照变化。");
        Console.WriteLine("结转复算验证通过：完整余额与确认、重复复算及父任务、输出差异拒绝、旧快照拒绝、输入重排摘要一致（5 项）。");
    }
    private static void Reject(Action action, string text)
    {
        try { action(); } catch (CrsException ex) when (ex.Message.Contains(text)) { return; }
        throw new Exception("复算未拒绝：" + text);
    }
    private sealed class Rates : IExchangeRateProvider
    {
        public string Fingerprint => "frozen";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()=>new(2026,1,1,12,0,0,TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone=>TimeZoneInfo.Utc;
    }
    private sealed class Importer(SecurityKey key) : IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)
        {
            var data = new ImportData();
            data.Sources.Add(new("report.xml","hash","A",new(2025,1,1),new(2025,12,31),true,true,true));
            data.Trades.Add(new(key,"STOCK",new(2025,2,1,12,0,0,TimeSpan.Zero),true,"SELL",1,10,10,.01m,9.99m,"SELL","report.xml",1,"STK",null));
            data.Positions.Add(new(key,"STOCK",new(2025,12,31),2));
            return data;
        }
    }
    private sealed class Validator : ICarryValidator
    {
        public void Validate(CarryDocument d,int year,string[] accounts)
        {
            if (d.Year != year - 1 || !d.Accounts.SequenceEqual(accounts)) throw new CrsException("结转年度和账户不匹配。");
        }
    }
    /// <summary>故意构造相同局部排序键，验证规范化快照使用完整记录排序。</summary>
    private sealed class SnapshotImporter(bool reverse) : IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)
        {
            var data = new ImportData(); var key = new SecurityKey("A","STOCK","USD");
            foreach (var hash in new[]{"one","two"})
                data.Sources.Add(new("a.xml",hash,"A",new(2025,1,1),new(2025,12,31),true,true,true));
            Trade Buy(string id,int row,decimal cost)=>new(key,"STOCK",new(2025,1,1,12,0,0,TimeSpan.Zero),true,"BUY",1,cost,cost,0,-cost,id,"a.xml",row,"STK",null);
            data.Trades.Add(Buy("Z",1,100)); data.Trades.Add(Buy("A",2,200));
            var sell = Buy("S",3,300) with { Time=new(2025,2,1,12,0,0,TimeSpan.Zero), Side="SELL", Net=300, ReportedPnl=200 };
            data.Trades.Add(sell); data.ReportedSales.Add(sell); data.Positions.Add(new(key,"STOCK",new(2025,12,31),1));
            foreach (var amount in new[]{10m,20m}) data.Cash.Add(new("A",new(2025,3,1),"Dividends","USD",amount,"D","a.xml"));
            data.Issues.Add(new("REVIEW","first")); data.Issues.Add(new("REVIEW","second"));
            if (reverse) { data.Trades.Reverse(); data.Sources.Reverse(); data.Cash.Reverse(); data.Issues.Reverse(); }
            return data;
        }
    }
    private sealed class Evidence : ICalculationEvidence
    {
        public string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
        public string HashJson(object value)=>Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        public string FileHash(string path)=>"hash";
        public string InfrastructureAssemblyHash=>"synthetic";
    }
}
