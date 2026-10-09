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
        var original = Service(originalRates, 2025).Calculate([], 2025, true, true, saveCanonicalSnapshot: true);
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
        Console.WriteLine("历史重放验证通过：冻结汇率与指纹、缺失汇率、无效汇率、执行口径、跨年状态、元数据门槛（6 项）。");
    }

    private static CalculationResult Clone(CalculationResult source, Action<JsonObject> change)
    {
        var node = JsonSerializer.SerializeToNode(source)!.AsObject(); change(node);
        return node.Deserialize<CalculationResult>()!;
    }

    private static CalculationService Service(FrozenRates rates, int currentYear) =>
        new(rates, new Importer(), new CarryValidator(), new Evidence(), timeProvider: new Clock(currentYear));

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

    private sealed class Importer : IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default)
        {
            var data = new ImportData();
            data.Sources.Add(new("synthetic", "synthetic", "A", new(2025, 1, 1), new(2025, 12, 31), true, true, true));
            data.Cash.Add(new("A", new(2025, 2, 1), "Dividends", "USD", 10m, "D", "synthetic"));
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
