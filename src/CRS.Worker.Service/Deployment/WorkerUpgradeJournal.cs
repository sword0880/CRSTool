using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Worker.Service;

/// <summary>Worker独立维护日志；不复用桌面插件升级状态，也不把“未观察到写入”当作无新事实证明。</summary>
public sealed record WorkerUpgradeRecord(int Format, string Id, string PreviousReleaseHash,
    string CandidateReleaseHash, string PreviousVersion, string CandidateVersion,
    string Stage, bool NewFactsPossible, DateTimeOffset UpdatedUtc, string? BackupManifestHash = null)
{
    public string? StorageMigrationReceiptHash {get;init;}
}

/// <summary>在打开密钥/数据库前持久标记风险。调用方必须持有数据目录的owner.lock。</summary>
public sealed class WorkerUpgradeJournal(string dataDirectory)
{
    private sealed record SnapshotManifest(int Format, string ReleaseHash, Dictionary<string, string> Files);
    private string JournalPath => Path.Combine(dataDirectory, "maintenance", "upgrade.json");
    private static readonly HashSet<string> Stages = ["Prepared", "BackedUp", "Staged", "Switched", "Starting",
        "Healthy", "Committed", "RollingBack", "RolledBack", "NeedsManualRepair"];
    public WorkerUpgradeRecord? Read()
    {
        StorageSecurity.RejectLinks(JournalPath);
        if (!File.Exists(JournalPath)) return null;
        // 状态查看不能用只共享读取的句柄阻断另一方原子替换日志。
        using var input = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 16384) throw new InvalidDataException("维护日志超限。");
        var record = JsonSerializer.Deserialize<WorkerUpgradeRecord>(input)
            ?? throw new InvalidDataException("维护日志缺失内容。");
        if (record.Format != 1 || !Guid.TryParseExact(record.Id, "N", out _) || !Stages.Contains(record.Stage)
            || !Hash(record.PreviousReleaseHash) || !Hash(record.CandidateReleaseHash)
            || !Version.TryParse(record.PreviousVersion, out _) || !Version.TryParse(record.CandidateVersion, out _)
            || record.BackupManifestHash is { } backup && !Hash(backup)
            || record.StorageMigrationReceiptHash is { } receipt && !Hash(receipt)
            || record.Stage is "Switched" or "Starting" or "Healthy" or "Committed" && record.BackupManifestHash is null
            || record.Stage is "Starting" or "Healthy" or "Committed" && !record.NewFactsPossible
            || record.Stage == "RolledBack" && record.NewFactsPossible)
            throw new InvalidDataException("维护日志格式或状态无效。");
        return record;
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static string ReleaseHash(string programDirectory)
    {
        var path = Path.Combine(programDirectory, "release.json");
        StorageSecurity.RejectLinks(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private void Write(WorkerUpgradeRecord record)
    {
        // 临时文件唯一且CreateNew；失败不覆盖原日志，重启仍按最近的持久阶段拒绝危险启动。
        StorageSecurity.RejectLinks(JournalPath);
        var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, record); output.Flush(true); }
        File.Move(temporary, JournalPath, true);
    }
    private void VerifyBackup(WorkerUpgradeRecord record)
    {
        var root = Path.Combine(dataDirectory, "maintenance", record.Id);
        var path = Path.Combine(root, "snapshot.json");
        StorageSecurity.RejectLinks(path);
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("升级快照清单超限。");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(record.BackupManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("升级快照清单哈希不一致。");
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(bytes) ?? throw new InvalidDataException("升级快照清单无效。");
        if (manifest.Format != 1 || manifest.ReleaseHash != record.PreviousReleaseHash || manifest.Files is null
            || manifest.Files.Count is < 2 or > 100000 || !manifest.Files.ContainsKey("service-key.dpapi") || !manifest.Files.ContainsKey("scheduler-config.db"))
            throw new InvalidDataException("升级快照清单不完整。");
        var snapshot = Path.GetFullPath(Path.Combine(root, "snapshot")) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in manifest.Files)
        {
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(x => x is "" or "." or "..") || !Hash(hash))
                throw new InvalidDataException("升级快照路径或哈希无效。");
            var file = Path.GetFullPath(Path.Combine(snapshot, relative));
            if (!file.StartsWith(snapshot, StringComparison.OrdinalIgnoreCase) || !seen.Add(file)) throw new InvalidDataException("升级快照路径越界或重复。");
            StorageSecurity.RejectLinks(file);
            using var input = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("升级快照文件已改变。");
        }
    }
    public void BeforeStorageOpen(string programDirectory)
    {
        var record = Read();
        if (record is null) return; // 无升级日志的首次安装与既有开发环境仍按原启动规则运行。
        var actual = ReleaseHash(programDirectory);
        var expected = record.Stage == "RolledBack" ? record.PreviousReleaseHash : record.CandidateReleaseHash;
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("程序发行与维护代次不一致，拒绝打开后台数据。");
        if (record.Stage is "Committed" or "RolledBack") return;
        if (record.Stage != "Switched" || record.BackupManifestHash is null)
            throw new InvalidOperationException("后台升级尚未解决，需要显式恢复；禁止自动重启改写数据。");
        VerifyBackup(record);
        Write(record with { Stage = "Starting", NewFactsPossible = true, StorageMigrationReceiptHash = null, UpdatedUtc = CRS.Timing.BeijingClock.Now });
    }
    internal void MarkStorageMigrationsReady(string programDirectory,string receiptHash)
    {
        var record=Read()??throw new InvalidOperationException("没有维护代次。");
        if(record.Stage!="Starting"||!record.NewFactsPossible||!ReleaseHash(programDirectory).Equals(record.CandidateReleaseHash,StringComparison.OrdinalIgnoreCase)||!Hash(receiptHash)
            ||WorkerStorageMigrations.FileHash(WorkerStorageMigrations.ReceiptPath(dataDirectory,record.Id))!=receiptHash)throw new InvalidDataException("存储维护回执没有绑定当前候选。");
        Write(record with{StorageMigrationReceiptHash=receiptHash,UpdatedUtc=CRS.Timing.BeijingClock.Now});
    }
    public void MarkHealthy(string programDirectory,IReadOnlyList<CRS.Worker.Abstractions.BackgroundPluginStatus> plugins,IReadOnlyList<string> expectedPlugins)
    {
        var record = Read();
        if (record is null || record.Stage is "Committed" or "RolledBack") return;
        if (record.Stage != "Starting" || !record.NewFactsPossible
            || !ReleaseHash(programDirectory).Equals(record.CandidateReleaseHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("候选升级状态与健康确认不一致。");
        // 普通运行继续隔离单个插件故障；维护确认必须证明本次加载的插件都已启动。
        if(plugins.Count>32||expectedPlugins.Count>32||plugins.Select(x=>x.PluginId).Distinct(StringComparer.Ordinal).Count()!=plugins.Count
            ||expectedPlugins.Distinct(StringComparer.Ordinal).Count()!=expectedPlugins.Count||plugins.Any(x=>x.State!="Started")
            ||!expectedPlugins.ToHashSet(StringComparer.Ordinal).SetEquals(plugins.Select(x=>x.PluginId)))throw new InvalidOperationException("候选插件未完整启动，不能确认维护健康。");
        if(WorkerStorageMigrationPlans.Read(programDirectory).Length>0&&(record.StorageMigrationReceiptHash is null
            ||WorkerStorageMigrations.FileHash(WorkerStorageMigrations.ReceiptPath(dataDirectory,record.Id))!=record.StorageMigrationReceiptHash))throw new InvalidDataException("候选存储维护尚未确认完成。");
        Write(record with { Stage = "Healthy", UpdatedUtc = CRS.Timing.BeijingClock.Now });
    }
}
