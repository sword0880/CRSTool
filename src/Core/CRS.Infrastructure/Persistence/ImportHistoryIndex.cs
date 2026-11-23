using System.Security.Cryptography;
using System.Text.Json;
using CRS.Domain;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CRS.Infrastructure;

/// <summary>在保险库内索引导入依据，历史封存正文保持原样。</summary>
internal static class ImportHistoryIndex
{
    internal const string Schema = """
        CREATE TABLE calculation_import_history(
          snapshot_id TEXT NOT NULL PRIMARY KEY REFERENCES calculations(id),
          broker TEXT NOT NULL, identity TEXT, details TEXT NOT NULL,
          archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0,1)), archive_group TEXT);
        CREATE INDEX calculation_import_identity ON calculation_import_history(identity,archived);
        CREATE INDEX calculation_import_broker ON calculation_import_history(broker);
        """ + ArchiveIndex;
    private const string ArchiveIndex = "CREATE INDEX calculation_archive_group ON calculation_import_history(archive_group,archived);";

    internal static void AddArchiveGroups(SqliteConnection c, SqliteTransaction tx)
    {
        c.CreateFunction<string,int,string,string?>("crs_archive_group", (broker,year,json) =>
        {
            try
            {
                var details = JsonSerializer.Deserialize<Details>(json);
                return details?.Sources is { Length: > 0 } sources
                    && sources.All(s => s?.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit))
                    ? ArchiveGroup(broker,year,sources) : null;
            }
            catch (JsonException) { return null; }
        },isDeterministic:true);
        c.Execute("""
            ALTER TABLE calculation_import_history ADD COLUMN archive_group TEXT;
            UPDATE calculation_import_history AS h SET archive_group=crs_archive_group(broker,
              (SELECT year FROM calculations WHERE id=h.snapshot_id),details);
            """ + ArchiveIndex,transaction:tx);
    }

    private const string Projection = """
        WITH bounded AS MATERIALIZED (
          SELECT id,year,CASE WHEN length(CAST(result_json AS BLOB)) BETWEEN 1 AND @MaxBytes
            THEN CASE WHEN json_valid(result_json) THEN result_json END END AS body
          FROM calculations WHERE @Id IS NULL OR id=@Id
        )
        SELECT id AS Id,year AS Year,json_extract(body,'$.Broker') AS Broker,
          json_extract(body,'$.Sources') AS Sources,json_extract(body,'$.SnapshotJson') AS Audit,
          json_extract(body,'$.OpeningZero') IS 1 AS OpeningZero,json_extract(body,'$.ScopeConfirmed') IS 1 AS ScopeConfirmed,
          json_extract(body,'$.IsReplayable') IS 1 AS IsReplayable,json_extract(body,'$.Provisional') IS 1 AS Provisional,
          json_extract(body,'$.TaxpayerScopeId') AS TaxpayerScopeId,
          json_extract(body,'$.AccountPeriods') AS AccountPeriods,json_extract(body,'$.ImportProvenance') AS Provenance,
          json_extract(body,'$.Rates') AS Rates
        FROM bounded
        WHERE json_extract(body,'$.Broker') IN ('IBKR','FUTU')
          AND json_extract(body,'$.ParentSnapshotId') IS NULL
          AND COALESCE(json_array_length(body,'$.AggregationSources'),0)=0
          AND json_array_length(body,'$.Sources')>0
        """;

    internal static void Add(SqliteConnection c, SqliteTransaction tx, string? id, bool allowDuplicate = true)
    {
        foreach (var row in c.Query<Snapshot>(Projection, new { Id = id, PersistedResultLimits.MaxBytes }, tx, buffered: false))
        {
            var details = Read(row);
            if (details is null) continue;
            var identity = Identity(row.Broker,details);
            if (!allowDuplicate && identity is not null && c.ExecuteScalar<int>(
                "SELECT EXISTS(SELECT 1 FROM calculation_import_history WHERE identity=@identity AND archived=0)",
                new { identity }, tx) != 0)
                throw new CrsException("另一项操作已保存相同资料的结果。请重试以打开已有结果。");
            c.Execute("""
                INSERT INTO calculation_import_history(snapshot_id,broker,identity,details,archive_group)
                VALUES(@Id,@Broker,@identity,@json,@archiveGroup)
                """, new { row.Id, row.Broker, identity, json = JsonSerializer.Serialize(details),
                    archiveGroup = ArchiveGroup(row.Broker,row.Year,details.Sources) }, tx);
        }
    }

    internal static ImportReview? Find(SqliteConnection c, SqliteTransaction tx, CalculationResult result, CancellationToken token)
    {
        var current = Read(new Snapshot
        {
            Year = result.Year, Sources = JsonSerializer.Serialize(result.Sources), Audit = result.SnapshotJson,
            OpeningZero = result.OpeningZero, ScopeConfirmed = result.ScopeConfirmed, Provisional = result.Provisional,
            IsReplayable = result.IsReplayable, TaxpayerScopeId = result.TaxpayerScopeId,
            AccountPeriods = JsonSerializer.Serialize(result.AccountPeriods), Provenance = JsonSerializer.Serialize(result.ImportProvenance),
            Rates = JsonSerializer.Serialize(result.Rates)
        });
        if (current is null) return null;
        var key = Identity(result.Broker,current);
        var archiveGroup = ArchiveGroup(result.Broker,result.Year,current.Sources);
        using var command = c.CreateCommand();
        // SQLite 逐行读取；只取索引摘要与提示所需字段。
        command.Transaction = tx;
        command.CommandText = """
            SELECT calculations.id,year,created_utc,complete,issues,archived,identity,details,
              CASE WHEN h.archive_group=$archiveGroup THEN (SELECT COUNT(*) FROM calculation_import_history AS copy
                WHERE copy.archive_group=$archiveGroup AND copy.archived=0) ELSE 0 END
            FROM calculation_import_history AS h JOIN calculations ON calculations.id=h.snapshot_id
            WHERE h.broker=$broker
            ORDER BY (identity=$key) DESC,archived,julianday(created_utc) DESC,calculations.id DESC
            """;
        command.Parameters.AddWithValue("$broker", result.Broker);
        command.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue("$archiveGroup", archiveGroup);
        using var rows = command.ExecuteReader();
        ImportReview? shared = null;
        ImportReview? overlapMatch = null;
        var sharedFileCount = 0;
        var newHashes = current.Sources.Select(s => s.Sha256).ToHashSet(StringComparer.Ordinal);
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            var existing = new HistoryItem(rows.GetString(0), rows.GetInt32(1), CRS.Timing.BeijingClock.NormalizeText(rows.GetString(2)),
                rows.GetBoolean(3), rows.GetInt32(4), result.Broker) { IsArchived = rows.GetBoolean(5), ActiveCopies = rows.GetInt32(8) };
            if (key is not null && !rows.IsDBNull(6) && rows.GetString(6) == key)
                return new(ImportMatchKind.Identical, existing, Names(current.Sources), []);

            var previous = JsonSerializer.Deserialize<Details>(rows.GetString(7))!;
            var knownHashes = previous.Sources.Select(s => s.Sha256).Where(newHashes.Contains).ToHashSet(StringComparer.Ordinal);
            var sameFiles = current.Files.SequenceEqual(previous.Files);
            if (sameFiles || knownHashes.Count > sharedFileCount)
            {
                sharedFileCount = knownHashes.Count;
                List<string> changes = current.Conditions is { } currentConditions && previous.Conditions is { } previousConditions
                    ? currentConditions.Where((value, i) => value != previousConditions[i]).Select(condition => condition.Label).ToList()
                    : ["历史核算条件未完整登记"];
                if (!sameFiles) changes.Insert(0, "报表组合或用途");
                shared = new(ImportMatchKind.SharedFiles, existing, Names(current.Sources.Where(s => knownHashes.Contains(s.Sha256))), changes);
                if (sameFiles) return shared;
            }
            if (shared is not null || overlapMatch is not null) continue;
            var byAccount = previous.Sources.Where(s => !s.Opening && s.Account.Length > 0).ToLookup(s => (s.Account, s.Purpose));
            var overlap = Names(current.Sources.Where(s => !s.Opening && s.Account.Length > 0 && s.Start <= s.End
                && byAccount[(s.Account, s.Purpose)].Any(p => p.Start <= p.End && p.Start <= s.End && s.Start <= p.End)));
            if (overlap.Length > 0) overlapMatch = new(ImportMatchKind.OverlappingPeriod, existing, overlap, []);
        }
        return shared ?? overlapMatch;
    }

    private static string[] Names(IEnumerable<SourceReport> sources)
        => sources.Select(s => s.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<ImportHistoryComparison> Compare(SqliteConnection c, SqliteTransaction tx, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rows = c.Query<ComparisonRow>("""
            WITH group_rows AS MATERIALIZED (
              SELECT h.snapshot_id,c.created_utc,c.complete,c.issues,h.archived
              FROM calculation_import_history AS h JOIN calculations AS c ON c.id=h.snapshot_id
              WHERE h.archive_group=(SELECT archive_group FROM calculation_import_history WHERE snapshot_id=@id)
              ORDER BY (h.snapshot_id=@id) DESC,h.archived,julianday(c.created_utc) DESC,h.snapshot_id DESC LIMIT 100
            )
            SELECT r.snapshot_id AS Id,r.created_utc AS CreatedUtc,r.complete AS Complete,r.issues AS Issues,r.archived AS IsArchived,
              CASE WHEN json_valid(h.details) THEN json_extract(h.details,'$.Conditions') END AS Conditions,
              CASE WHEN json_valid(h.details) THEN json_extract(h.details,'$.Files') END AS FileRoles,
              CASE WHEN length(CAST(c.result_json AS BLOB))<=@MaxBytes
                THEN CASE WHEN json_valid(c.result_json) THEN json_extract(c.result_json,'$.Summary') END END AS Summary
            FROM group_rows AS r JOIN calculations AS c ON c.id=r.snapshot_id
              JOIN calculation_import_history AS h ON h.snapshot_id=r.snapshot_id
            ORDER BY (r.snapshot_id=@id) DESC,r.archived,julianday(r.created_utc) DESC,r.snapshot_id DESC
            """,new { id, PersistedResultLimits.MaxBytes },tx).ToArray();
        if (rows.Length < 2) throw new CrsException("当前任务暂无可比较的同批记录。");
        var baseline = ReadJson<Condition[]>(rows[0].Conditions);
        return rows.Select(row =>
        {
            token.ThrowIfCancellationRequested();
            var selected = row.Id == id;
            var conditions = ReadJson<Condition[]>(row.Conditions);
            string[] differences = selected ? []
                : baseline is null || conditions is null ? ["部分历史核算条件未登记"]
                : baseline.Except(conditions).Concat(conditions.Except(baseline)).Select(condition => condition.Label).Distinct().ToArray();
            if (!selected && row.FileRoles != rows[0].FileRoles) differences = ["报表用途", ..differences];
            var summary = ReadJson<TaxSummary>(row.Summary);
            return new ImportHistoryComparison(row.Id,CRS.Timing.BeijingClock.NormalizeText(row.CreatedUtc),row.Complete,row.Issues,row.IsArchived,
                summary?.GainCny,summary?.EstimatedTopUpCny,differences,selected);
        }).ToArray();
    }

    private static T? ReadJson<T>(string? json)
    {
        try { return json is null ? default : JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    private static Details? Read(Snapshot row)
    {
        try
        {
            var sources = JsonSerializer.Deserialize<SourceReport[]>(row.Sources) ?? [];
            if (sources.Length == 0 || sources.Any(s => s is null || s.Sha256 is not { Length: 64 }
                || !s.Sha256.All(Uri.IsHexDigit) || string.IsNullOrEmpty(s.File) || s.Account is null)) return null;
            sources = sources.Select(s => s with { Sha256 = s.Sha256.ToLowerInvariant() }).ToArray();
            var files = sources.Select(s => $"{s.Purpose}:{s.Opening}:{s.Sha256}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new(sources, files, ReadConditions(row));
        }
        catch (JsonException) { return null; }
    }

    private static Condition[]? ReadConditions(Snapshot row)
    {
        try
        {
            using var audit = JsonDocument.Parse(row.Audit);
            var root = audit.RootElement;
            var policy = root.GetProperty("policy").GetString();
            var algorithm = root.GetProperty("algorithm").GetString();
            var configuration = root.GetProperty("configuration").GetString();
            if (string.IsNullOrEmpty(policy) || string.IsNullOrEmpty(algorithm) || string.IsNullOrEmpty(configuration)
                || !root.TryGetProperty("carryFingerprint", out var carry)) return null;

            var periods = JsonSerializer.Deserialize<AccountPeriodEvidence[]>(row.AccountPeriods ?? "null") ?? [];
            var versions = JsonSerializer.Deserialize<TaxImportProvenance[]>(row.Provenance ?? "null") ?? [];
            var rates = JsonSerializer.Deserialize<AppliedRate[]>(row.Rates ?? "null") ?? [];
            if (periods.Any(p => p is null) || versions.Any(v => v is null) || rates.Any(r => r is null)) return null;
            return [
                new("核算年度", Digest(row.Year)),
                new("期初资料或确认", Digest(new { row.OpeningZero, Carry = carry.GetString() })),
                new("申报范围确认", Digest(new { row.ScopeConfirmed, row.TaxpayerScopeId })),
                new("账户期间依据", Digest(periods.OrderBy(p => p.Account, StringComparer.Ordinal))),
                new("汇率配置或汇率", Digest(new { configuration, Rates = rates.OrderBy(r => r.Currency, StringComparer.Ordinal) })),
                new("计算或解析口径", Digest(new { policy, algorithm, row.Provisional, Versions = versions.OrderBy(v => v.SourceFileHash, StringComparer.Ordinal) })),
                new("核算结果或状态", Digest(new { Summary = root.GetProperty("summary"), Complete = root.GetProperty("Complete") })),
                new("完整快照选项", Digest(row.IsReplayable))
            ];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // 仍可提示同一原件，缺少核算依据时不能断定两次完全相同。
            return null;
        }
    }

    private static string? Identity(string broker, Details details) => details.Conditions is null
        ? null : Digest(new { Broker = broker, details.Files, details.Conditions });

    // 归档按整批原件归组，导入去重仍使用包含核算条件的 Identity。
    private static string ArchiveGroup(string broker, int year, SourceReport[] sources)
        => Digest(new { broker, year, hashes = sources.Select(s => s.Sha256.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private sealed record Details(SourceReport[] Sources, string[] Files, Condition[]? Conditions);
    private sealed record Condition(string Label, string Digest);

    private sealed class ComparisonRow
    {
        public string Id { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public bool Complete { get; set; }
        public int Issues { get; set; }
        public bool IsArchived { get; set; }
        public string? Conditions { get; set; }
        public string? FileRoles { get; set; }
        public string? Summary { get; set; }
    }

    private sealed class Snapshot
    {
        public string Id { get; set; } = "";
        public string Broker { get; set; } = "";
        public int Year { get; set; }
        public string Sources { get; set; } = "[]";
        public string Audit { get; set; } = "";
        public bool OpeningZero { get; set; }
        public bool ScopeConfirmed { get; set; }
        public bool IsReplayable { get; set; }
        public bool Provisional { get; set; }
        public string TaxpayerScopeId { get; set; } = "";
        public string? AccountPeriods { get; set; }
        public string? Provenance { get; set; }
        public string? Rates { get; set; }
    }
}
