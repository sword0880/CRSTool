using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>真实公开NAV原件在隔离SQLCipher库的存储验收；使用显式测试密钥，完全不连接正式Worker。</summary>
internal static class NavStorageTool
{
    public static void Run(string snapshot, string output)
    {
        var documents = NavResearchTool.Documents(snapshot); var final = Path.GetFullPath(output);
        if (Directory.Exists(final) || File.Exists(final)) throw new IOException("输出已存在，拒绝覆盖。");
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        var raw = new RawEvidenceStore(Path.Combine(staging, "raw")); var data = Path.Combine(staging, "data");
        var id = Guid.NewGuid(); File.WriteAllText(Path.Combine(staging, "command-id.txt"), id.ToString("D"));
        var key = new EtfKey("SZSE", "159915"); var from = new DateOnly(2011, 9, 20); var through = new DateOnly(2026, 10, 23);
        var before = DateTimeOffset.UtcNow; NavImportOutcome imported;
        using (var repository = TestMarketData.Open(data, raw)) imported = repository.ImportNavDocuments(documents, id);
        IReadOnlyList<NavAcceptedPoint> points;
        using (var reopened = TestMarketData.Open(data, raw))
        {
            points = reopened.NavPoints(key, from, through, DateTimeOffset.UtcNow);
            if (points.Count != 3635 || reopened.NavPoints(key, from, through, before).Count != 0 || reopened.ImportNavDocuments(documents, id) != imported)
                throw new InvalidDataException("真实NAV隔离存储、首见边界或重启重放验证失败。");
            if (reopened.CaptureIntegrityEvidence().Count != 4) throw new InvalidDataException("NAV原件未完整进入正式巡检契约。");
        }
        var report = new { CheckedAt = DateTimeOffset.UtcNow, Installed = false, TestKeyOnly = true, CommandId = id, Import = imported,
            ReopenedPoints = points.Count, First = points[0], Last = points[^1], BeforeRegistrationPoints = 0,
            SourceIntegrityDocuments = 4, HistoricalPublicationTime = "Unknown", GoalAchieved = false };
        File.WriteAllText(Path.Combine(staging, "storage-acceptance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Directory.Move(staging, final);
        Console.WriteLine(JsonSerializer.Serialize(new { Output = final, imported.Added, ReopenedPoints = points.Count, SourceIntegrityDocuments = 4,
            BeforeRegistrationPoints = 0, Installed = false, GoalAchieved = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
