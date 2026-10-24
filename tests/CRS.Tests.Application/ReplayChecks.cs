using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ReplayChecks
{
    public static void Run()
    {
        var originalRates = new FrozenRates(7m, "historical-config");
        var original = Service(originalRates, 2025).Calculate(["synthetic"], 2025, true, true, saveCanonicalSnapshot: true);
        var currentRates = new FrozenRates(99m, "changed-config", forbidAccess: true);
        var service = Service(currentRates, 2026);
        var replay = service.Replay(original);
        Assert(replay.Summary == original.Summary && replay.Rates.SequenceEqual(original.Rates),
            "历史重放采用了当前汇率。");
        using (var audit = JsonDocument.Parse(replay.SnapshotJson))
            Assert(audit.RootElement.GetProperty("configuration").GetString() == "historical-config",
                "重放审计记录采用了当前配置指纹。");

        Reject(() => service.Replay(Clone(original, node => node["Rates"] = new JsonArray())), "缺少");
        Reject(() => service.Replay(Clone(original, node =>
        {
            var rows = node["Rates"]!.AsArray(); rows.Add(rows[0]!.DeepClone());
        })), "重复");
        Reject(() => service.Replay(Clone(original, node => node["Rates"]![0]!["Value"] = 0m)), "无效");
        Reject(() => service.Replay(Clone(original, node => node["Rates"]![0]!["Year"] = 2024)), "错年度");

        var policy = Clone(original, node =>
        {
            var audit = JsonNode.Parse(node["SnapshotJson"]!.GetValue<string>())!.AsObject();
            audit["policy"] = "UNKNOWN-POLICY"; node["SnapshotJson"] = audit.ToJsonString();
        });
        Reject(() => service.Replay(policy), "不兼容");
        var algorithm = Clone(original, node =>
        {
            var audit = JsonNode.Parse(node["SnapshotJson"]!.GetValue<string>())!.AsObject();
            audit["algorithm"] = "UNKNOWN-ALGORITHM"; node["SnapshotJson"] = audit.ToJsonString();
        });
        Reject(() => service.Replay(algorithm), "不兼容");

        Assert(original.Provisional && replay.Provisional && replay.UsageLabel == original.UsageLabel
            && replay.CarryEligible == original.CarryEligible,
            "跨年重放静默将临时结果升级为正式结果。");
        Reject(() => service.Replay(Clone(original, node => node["SnapshotJson"] = "")), "元数据");
        CheckBeijingYearBoundary();
        Console.WriteLine("历史重放验证通过：冻结汇率与指纹、缺失汇率、无效汇率、执行口径、跨年状态、元数据门槛（6 项）。");
    }

    private static CalculationResult Clone(CalculationResult source, Action<JsonObject> change)
    {
        var node = JsonSerializer.SerializeToNode(source)!.AsObject();
        // 本组专门验证旧记录的语义门槛；新封存的篡改另有独立测试。
        node.Remove(nameof(CalculationResult.EvidenceSeal)); change(node);
        return node.Deserialize<CalculationResult>()!;
    }

    private static CalculationService Service(FrozenRates rates, int currentYear) =>
        new(rates, new Importer(), new CarryValidator(), new Evidence(), timeProvider: new Clock(currentYear));

    private static void CheckBeijingYearBoundary()
    {
        // 北京跨年时，宿主可能仍在上一年，也可能已经跨年；年度状态必须只随业务时钟变化。
        var boundary = new DateTimeOffset(2025, 12, 31, 16, 0, 0, TimeSpan.Zero);
        var rates = new FrozenRates(7m, "boundary-config");
        var checks = 0;
        CalculationService At(DateTimeOffset instant, int offset, int dataYear=2025) => new(rates, new Importer(dataYear),
            new CarryValidator(), new Evidence(), timeProvider: new ZonedClock(instant, offset));
        foreach (var offset in new[] { -12, 0, 14 })
        {
            var before = At(boundary.AddTicks(-1), offset).Calculate(["synthetic"], 2025, true, true, saveCanonicalSnapshot: true);
            var at = At(boundary, offset).Calculate(["synthetic"], 2025, true, true, saveCanonicalSnapshot: true);
            var after = At(boundary.AddTicks(1), offset).Calculate(["synthetic"], 2025, true, true);
            Assert(before.Provisional && !at.Provisional && !after.Provisional, "业务跨年边界受宿主时区影响。"); checks++;
            Assert(before.Summary == at.Summary && at.Summary == after.Summary, "时间口径变更影响了计算金额。"); checks++;
            Assert(At(boundary, offset, 2026).Calculate(["synthetic"], 2026, true, true).Provisional,
                "当前业务年度未保留临时状态。"); checks++;
            // 历史快照已经冻结的状态不随重放时刻改变，包括反向重放到跨年前的情形。
            Assert(At(boundary, offset).Replay(before).Provisional &&
                !At(boundary.AddTicks(-1), offset).Replay(at).Provisional, "重放重新推断了历史临时状态。"); checks++;
        }
        Console.WriteLine($"北京时间年度验证通过：跨年前后、三种宿主时区、金额不变与冻结重放（{checks} 项）。");
    }

    // 故意提供与业务时区不同的本地时区，用于发现 GetLocalNow 回归。
    private sealed class ZonedClock(DateTimeOffset instant, int offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(offset), "fixture", "fixture");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string expected)
    {
        try { action(); } catch (CrsException ex) when (ex.Message.Contains(expected)) { return; }
        throw new Exception($"历史重放未拒绝：{expected}。");
    }

    private sealed class FrozenRates(decimal value, string fingerprint, bool forbidAccess = false) : IExchangeRateProvider
    {
        public string Fingerprint => forbidAccess ? throw new Exception("重放读取了当前配置指纹。") : fingerprint;
        public AppliedRate Get(int year, string currency) => forbidAccess
            ? throw new Exception("重放读取了当前汇率。")
            : new(year, currency, value, "synthetic", "", "", null, null, false, true);
    }

    private sealed class Clock(int year) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(year, 6, 1, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Importer(int year=2025) : IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default)
        {
            var data = new ImportData();
            data.Sources.Add(new("synthetic", new string('a',64), "A", new(year, 1, 1), new(year, 12, 31), true, true, true));
            data.Cash.Add(new("A", new(year, 2, 1), "Dividends", "USD", 10m, "D", "synthetic"));
            return data;
        }
    }

    private sealed class CarryValidator : ICarryValidator
    {
        public void Validate(CarryDocument document, int year, string[] accounts) { }
    }

    private sealed class Evidence : ICalculationEvidence
    {
        public string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public string HashJson(object value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        public string FileHash(string path) => "synthetic";
        public string InfrastructureAssemblyHash => "synthetic";
    }
}
