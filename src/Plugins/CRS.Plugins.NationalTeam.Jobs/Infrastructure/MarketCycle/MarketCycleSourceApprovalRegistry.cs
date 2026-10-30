// 周期来源审批的持久记录与防重放控制；审批内容、许可原件、授权身份和审计链一起核对，认证密钥用于完整性而非公开研究库加密。
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository : IMarketCycleSourceApprovalRegistry
{
    private readonly byte[] marketCycleApprovalAuditKey = [];
    private readonly IMarketCycleApprovalWriteAuthority? marketCycleApprovalWriteAuthority = null;
    private const string ApprovalAuditGenesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private static byte[] DeriveMarketCycleApprovalAuditKey(ReadOnlySpan<byte> databaseKey) =>
        HMACSHA256.HashData(databaseKey, Encoding.UTF8.GetBytes("CRSTool.MarketCycleApprovalAudit.v1"));

    private sealed record ApprovalAuditAnchorPayload(int Version, long Sequence, string EventHash);

    private sealed record ApprovalAuditPayload(
        long Sequence,
        string CommandId,
        string Nonce,
        string SourceId,
        string DatasetId,
        string Status,
        string ActorId,
        string RecordedAt,
        string IssuedAt,
        string ApprovalJson,
        string PayloadHash,
        string PreviousEventHash);

    private sealed record StoredApprovalEvent(
        long Sequence,
        Guid CommandId,
        string Nonce,
        string SourceId,
        string DatasetId,
        string Status,
        string ActorId,
        string RecordedAt,
        string IssuedAt,
        string ApprovalJson,
        string PayloadHash,
        string PreviousEventHash,
        string EventHash)
    {
        public DateTimeOffset RecordedAtValue => ParseTime(RecordedAt);
        public DateTimeOffset IssuedAtValue => ParseTime(IssuedAt);
    }

    private static DateTimeOffset ParseTime(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw new InvalidDataException("来源审批审计时间格式无效。");
        return parsed;
    }

    private void InitializeMarketCycleApprovalSchema()
    {
        var version = Scalar("SELECT version FROM feature_schema_migration WHERE feature='market-cycle-approval'");
        var tableCount = Convert.ToInt32(Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='market_cycle_source_approval_event'"));
        var anchorTableCount = Convert.ToInt32(Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='market_cycle_source_approval_anchor'"));
        if (version is not null && (Convert.ToInt32(version) != 2 || tableCount != 1 || anchorTableCount != 1) ||
            version is null && (tableCount != 0 || anchorTableCount != 0))
            throw new InvalidDataException("市场周期来源审批迁移与表结构不一致，拒绝自动重建。");
        if (version is not null) return;

        beforeWrite?.Invoke();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS market_cycle_source_approval_event(
                sequence INTEGER PRIMARY KEY,
                command_id TEXT NOT NULL UNIQUE,
                nonce TEXT NOT NULL UNIQUE,
                source_id TEXT NOT NULL,
                dataset_id TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('Approved','Rejected','Revoked')),
                actor_id TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                approval_json TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                previous_event_hash TEXT NOT NULL,
                event_hash TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS ix_market_cycle_approval_dataset
                ON market_cycle_source_approval_event(dataset_id, sequence);
            CREATE TABLE IF NOT EXISTS market_cycle_source_approval_anchor(
                id INTEGER PRIMARY KEY CHECK(id=1),
                sequence INTEGER NOT NULL CHECK(sequence>0),
                event_hash TEXT NOT NULL,
                anchor_mac TEXT NOT NULL
            );
            INSERT INTO feature_schema_migration(feature,version,applied_at)
                VALUES('market-cycle-approval',2,$at);
            """;
        command.Parameters.AddWithValue("$at", Now);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Writes are disabled unless the repository was created by a trusted host with an approval authority.
    /// Authorization proof is checked in memory and is never persisted or logged.
    /// </summary>
    public MarketCycleSourceApprovalWriteReceipt Apply(MarketCycleSourceApprovalWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        beforeWrite?.Invoke();
        if (marketCycleApprovalWriteAuthority is null)
            throw new UnauthorizedAccessException("未注入可信管理员授权器；市场周期来源审批写入默认关闭。");

        var now = clock.GetUtcNow();
        ValidateApprovalCommand(command, now);
        if (!marketCycleApprovalWriteAuthority.TryAuthorize(command, now, out var actor) ||
            !IsSafeToken(actor, 100) ||
            !string.Equals(actor, command.Approval.Reviewer, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("市场周期来源审批未通过可信管理员授权。");

        if (!raw.Verify(command.Approval.TermsEvidenceHash))
            throw new InvalidDataException("来源许可/条款原件不存在或 SHA-256 核验失败。");

        var approvalJson = JsonSerializer.Serialize(command.Approval);
        var payloadHash = HashText(approvalJson);
        var commandId = command.CommandId.ToString("N");
        var issuedAt = command.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        using var transaction = db.BeginTransaction();
        var events = ReadVerifiedApprovalEvents(transaction);
        // Idempotent retries are checked before the global time watermark: a still-fresh exact retry
        // must return its original receipt even if a later independent command has already been committed.
        var existing = events.SingleOrDefault(x => x.CommandId == command.CommandId);
        if (existing is not null)
        {
            if (existing.PayloadHash != payloadHash ||
                existing.Nonce != command.Nonce ||
                existing.IssuedAt != issuedAt ||
                existing.ApprovalJson != approvalJson)
                throw new InvalidDataException("已使用的命令 ID 不能替换审批内容、时间或 nonce。");
            transaction.Commit();
            return new(command.CommandId, existing.Sequence, existing.Status, existing.EventHash, Replayed: true);
        }

        var lastGlobalEvent = events.LastOrDefault();
        if (lastGlobalEvent is not null &&
            (now < lastGlobalEvent.RecordedAtValue || command.IssuedAt < lastGlobalEvent.IssuedAtValue))
            throw new InvalidDataException("来源审批全局审计时间水位倒退，拒绝写入。");

        if (events.Any(x => string.Equals(x.Nonce, command.Nonce, StringComparison.Ordinal)))
            throw new InvalidDataException("来源审批 nonce 已使用，拒绝重放。");

        var datasetEvents = events.Where(x => string.Equals(x.DatasetId, command.Approval.DatasetId, StringComparison.Ordinal)).ToArray();
        if (datasetEvents.Any(x => !string.Equals(x.SourceId, command.Approval.SourceId, StringComparison.Ordinal)))
            throw new InvalidDataException("数据集 ID 已绑定到另一个来源，拒绝重新绑定。");

        var sourceEvents = events.Where(x => string.Equals(x.SourceId, command.Approval.SourceId, StringComparison.Ordinal)).ToArray();
        foreach (var item in sourceEvents)
        {
            var priorApproval = DeserializeApproval(item.ApprovalJson);
            if (!string.Equals(priorApproval.SourceUri.AbsoluteUri, command.Approval.SourceUri.AbsoluteUri, StringComparison.Ordinal))
                throw new InvalidDataException("来源 ID 已绑定到另一个基址；地址变更必须使用新来源 ID 并重新审核。");
        }

        var previousForDataset = datasetEvents.LastOrDefault();
        if (command.Approval.Status == MarketCycleSourceApprovalStatus.Revoked && previousForDataset is null)
            throw new InvalidOperationException("没有既有批准记录的来源不能直接登记为已撤销。");
        if (previousForDataset is not null)
        {
            if (now < previousForDataset.RecordedAtValue ||
                command.IssuedAt <= previousForDataset.IssuedAtValue ||
                command.Approval.ReviewedAt <= previousForDataset.RecordedAtValue)
                throw new InvalidDataException("审批时间水位倒退或复用了旧审批，拒绝写入。");

            var previousApproval = DeserializeApproval(previousForDataset.ApprovalJson);
            if (previousApproval == command.Approval)
                throw new InvalidDataException("审批内容未发生变化；拒绝生成重复的审批版本。");
            if (command.Approval.Status == MarketCycleSourceApprovalStatus.Revoked &&
                previousApproval.Status != MarketCycleSourceApprovalStatus.Approved)
                throw new InvalidOperationException("只有当前已批准的来源才能被撤销。");
            if (command.Approval.Status == MarketCycleSourceApprovalStatus.Rejected &&
                previousApproval.Status == MarketCycleSourceApprovalStatus.Approved)
                throw new InvalidOperationException("已批准来源必须使用 Revoked 事件撤销，不能改写成 Rejected。");
        }

        var sequence = checked((long)events.Count + 1);
        var previousHash = events.LastOrDefault()?.EventHash ?? ApprovalAuditGenesis;
        var recordedAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var auditPayload = new ApprovalAuditPayload(
            sequence, commandId, command.Nonce, command.Approval.SourceId, command.Approval.DatasetId,
            command.Approval.Status.ToString(), actor, recordedAt, issuedAt, approvalJson, payloadHash, previousHash);
        var eventHash = ComputeAuditHash(auditPayload);

        using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO market_cycle_source_approval_event(
                sequence,command_id,nonce,source_id,dataset_id,status,actor_id,recorded_at,issued_at,
                approval_json,payload_hash,previous_event_hash,event_hash)
            VALUES($sequence,$command,$nonce,$source,$dataset,$status,$actor,$recorded,$issued,
                $approval,$payload,$previous,$event)
            """;
        insert.Parameters.AddWithValue("$sequence", sequence);
        insert.Parameters.AddWithValue("$command", commandId);
        insert.Parameters.AddWithValue("$nonce", command.Nonce);
        insert.Parameters.AddWithValue("$source", command.Approval.SourceId);
        insert.Parameters.AddWithValue("$dataset", command.Approval.DatasetId);
        insert.Parameters.AddWithValue("$status", command.Approval.Status.ToString());
        insert.Parameters.AddWithValue("$actor", actor);
        insert.Parameters.AddWithValue("$recorded", recordedAt);
        insert.Parameters.AddWithValue("$issued", issuedAt);
        insert.Parameters.AddWithValue("$approval", approvalJson);
        insert.Parameters.AddWithValue("$payload", payloadHash);
        insert.Parameters.AddWithValue("$previous", previousHash);
        insert.Parameters.AddWithValue("$event", eventHash);
        insert.ExecuteNonQuery();

        var anchorMac = ComputeApprovalAnchorMac(sequence, eventHash);
        using var anchor = db.CreateCommand();
        anchor.Transaction = transaction;
        anchor.CommandText = """
            INSERT INTO market_cycle_source_approval_anchor(id,sequence,event_hash,anchor_mac)
            VALUES(1,$sequence,$event,$mac)
            ON CONFLICT(id) DO UPDATE SET
                sequence=excluded.sequence,event_hash=excluded.event_hash,anchor_mac=excluded.anchor_mac
            """;
        anchor.Parameters.AddWithValue("$sequence", sequence);
        anchor.Parameters.AddWithValue("$event", eventHash);
        anchor.Parameters.AddWithValue("$mac", anchorMac);
        anchor.ExecuteNonQuery();

        transaction.Commit();
        return new(command.CommandId, sequence, command.Approval.Status.ToString(), eventHash);
    }

    public MarketCycleSourceApproval? Find(string datasetId)
    {
        if (!IsSafeToken(datasetId, 100))
            throw new InvalidDataException("数据集 ID 格式无效。");
        var events = ReadVerifiedApprovalEvents();
        var matching = events.Where(x => string.Equals(x.DatasetId, datasetId, StringComparison.Ordinal)).ToArray();
        if (matching.Length == 0) return null;
        if (matching.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("数据集审批历史包含多个来源身份，拒绝选择其中一个。");
        return DeserializeApproval(matching[^1].ApprovalJson);
    }

    public bool Verify(string sha256) => raw.Verify(sha256);

    public IReadOnlyList<MarketCycleSourceApprovalAuditEntry> ReadAudit() =>
        ReadVerifiedApprovalEvents().Select(x => new MarketCycleSourceApprovalAuditEntry(
            x.Sequence, x.CommandId, x.SourceId, x.DatasetId,
            Enum.Parse<MarketCycleSourceApprovalStatus>(x.Status, ignoreCase: false),
            x.ActorId, x.RecordedAtValue, x.PayloadHash, x.PreviousEventHash, x.EventHash)).ToArray();

    public bool VerifyAuditChain()
    {
        try
        {
            _ = ReadVerifiedApprovalEvents();
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or
            CryptographicException or InvalidCastException or OverflowException or SqliteException)
        {
            return false;
        }
    }

    private List<StoredApprovalEvent> ReadVerifiedApprovalEvents(SqliteTransaction? transaction = null)
    {
        var rows = new List<StoredApprovalEvent>();
        using (var command = db.CreateCommand())
        {
            if (transaction is not null) command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence,command_id,nonce,source_id,dataset_id,status,actor_id,recorded_at,issued_at,
                    approval_json,payload_hash,previous_event_hash,event_hash
                FROM market_cycle_source_approval_event ORDER BY sequence
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(1), "N", out var commandId) || commandId == Guid.Empty)
                    throw new InvalidDataException("审批审计命令 ID 损坏。");
                rows.Add(new(
                    reader.GetInt64(0), commandId, reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)));
            }
        }

        var previous = ApprovalAuditGenesis;
        DateTimeOffset? previousRecordedAt = null;
        DateTimeOffset? previousIssuedAt = null;
        long expectedSequence = 1;
        foreach (var row in rows)
        {
            if (row.Sequence != expectedSequence ||
                row.PreviousEventHash != previous ||
                row.Nonce.Length is < 16 or > 128 ||
                !IsSafeToken(row.Nonce, 128) ||
                !IsSafeToken(row.SourceId, 100) ||
                !IsSafeToken(row.DatasetId, 100) ||
                !IsSafeToken(row.ActorId, 100) ||
                !Enum.TryParse<MarketCycleSourceApprovalStatus>(row.Status, ignoreCase: false, out var status) ||
                status is MarketCycleSourceApprovalStatus.Pending ||
                !Enum.IsDefined(status))
                throw new InvalidDataException("来源审批审计链序号、身份或状态无效。");

            var approval = DeserializeApproval(row.ApprovalJson);
            if (approval.SourceId != row.SourceId ||
                approval.DatasetId != row.DatasetId ||
                approval.Status != status ||
                approval.Reviewer != row.ActorId ||
                HashText(row.ApprovalJson) != row.PayloadHash)
                throw new InvalidDataException("来源审批审计载荷与索引字段或 SHA-256 不一致。");

            var payload = new ApprovalAuditPayload(
                row.Sequence, row.CommandId.ToString("N"), row.Nonce, row.SourceId, row.DatasetId,
                row.Status, row.ActorId, row.RecordedAt, row.IssuedAt, row.ApprovalJson,
                row.PayloadHash, row.PreviousEventHash);
            var expectedHash = ComputeAuditHash(payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(expectedHash), Convert.FromHexString(row.EventHash)))
                    throw new InvalidDataException("来源审批审计 HMAC 不匹配，拒绝读取。");
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("来源审批审计 HMAC 格式无效。", error);
            }

            var recordedAtValue = row.RecordedAtValue;
            var issuedAtValue = row.IssuedAtValue;
            if (previousRecordedAt is { } priorRecorded && recordedAtValue < priorRecorded ||
                previousIssuedAt is { } priorIssued && issuedAtValue < priorIssued)
                throw new InvalidDataException("来源审批审计时间水位倒退。");
            previousRecordedAt = recordedAtValue;
            previousIssuedAt = issuedAtValue;
            previous = row.EventHash;
            expectedSequence++;
        }
        VerifyApprovalAnchor(rows, transaction);
        return rows;
    }

    private void VerifyApprovalAnchor(IReadOnlyList<StoredApprovalEvent> events, SqliteTransaction? transaction)
    {
        try
        {
            using var command = db.CreateCommand();
            if (transaction is not null) command.Transaction = transaction;
            command.CommandText = "SELECT sequence,event_hash,anchor_mac FROM market_cycle_source_approval_anchor WHERE id=1";
            using var reader = command.ExecuteReader();
            if (events.Count == 0)
            {
                if (reader.Read())
                    throw new InvalidDataException("无审批事件时不应存在审计链头锚点。");
                return;
            }

            if (!reader.Read())
                throw new InvalidDataException("来源审批审计链头锚点缺失，可能发生尾部截断。");
            var sequence = reader.GetInt64(0);
            var eventHash = reader.GetString(1);
            var anchorMac = reader.GetString(2);
            if (reader.Read())
                throw new InvalidDataException("来源审批审计链头锚点不唯一。");

            var latest = events[^1];
            if (sequence != latest.Sequence || !string.Equals(eventHash, latest.EventHash, StringComparison.Ordinal))
                throw new InvalidDataException("来源审批审计链头锚点与最新事件不一致，可能发生尾部截断或回滚。");

            var expectedMac = ComputeApprovalAnchorMac(sequence, eventHash);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(expectedMac), Convert.FromHexString(anchorMac)))
                    throw new InvalidDataException("来源审批审计链头锚点 HMAC 不匹配。");
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("来源审批审计链头锚点 HMAC 格式无效。", error);
            }
        }
        catch (SqliteException error)
        {
            throw new InvalidDataException("来源审批审计链头锚点缺失或不可读取。", error);
        }
    }

    private string ComputeApprovalAnchorMac(long sequence, string eventHash) =>
        Convert.ToHexString(HMACSHA256.HashData(
            marketCycleApprovalAuditKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ApprovalAuditAnchorPayload(1, sequence, eventHash)))));

    private static void ValidateApprovalCommand(MarketCycleSourceApprovalWriteCommand command, DateTimeOffset now)
    {
        if (command.CommandId == Guid.Empty ||
            !IsSafeToken(command.Nonce, 128) || command.Nonce.Length < 16 ||
            string.IsNullOrWhiteSpace(command.AuthorizationProof) || command.AuthorizationProof.Length > 4096 ||
            command.IssuedAt == default || command.IssuedAt > now.AddSeconds(30) ||
            now - command.IssuedAt > TimeSpan.FromMinutes(5))
            throw new InvalidDataException("审批命令 ID、nonce、授权证明或新鲜度无效。");

        var approval = command.Approval ?? throw new InvalidDataException("审批记录不能为空。");
        if (!IsSafeToken(approval.SourceId, 100) ||
            !IsSafeToken(approval.DatasetId, 100) ||
            !IsSafeToken(approval.ContractVersion, 40) ||
            !IsSafeToken(approval.ParserVersion, 40) ||
            !IsSafeToken(approval.PolicyVersion, 40))
            throw new InvalidDataException("审批来源、数据集或版本标识无效。");

        if (approval.SourceUri is null || !approval.SourceUri.IsAbsoluteUri ||
            approval.SourceUri.Scheme != Uri.UriSchemeHttps || !approval.SourceUri.IsDefaultPort ||
            string.IsNullOrWhiteSpace(approval.SourceUri.Host) || approval.SourceUri.UserInfo.Length != 0 ||
            approval.SourceUri.Query.Length != 0 || approval.SourceUri.Fragment.Length != 0 ||
            !MarketCycleSourceAdmission.IsSafeUriPath(approval.SourceUri))
            throw new InvalidDataException("审批来源必须是无凭据、无查询参数、无片段的绝对 HTTPS 基址。");

        if (string.IsNullOrEmpty(approval.TermsEvidenceHash) || approval.TermsEvidenceHash.Length != 64 ||
            approval.TermsEvidenceHash.Any(c => !Uri.IsHexDigit(c)) ||
            approval.TermsEvidenceHash != approval.TermsEvidenceHash.ToUpperInvariant())
            throw new InvalidDataException("许可/条款证据必须使用大写 SHA-256 哈希。");

        const MarketCycleSourceUse knownUses = MarketCycleSourceUse.AutomatedCollection |
            MarketCycleSourceUse.PersistentStorage | MarketCycleSourceUse.HistoricalBackfill |
            MarketCycleSourceUse.Redistribution;
        if ((approval.ApprovedUses & ~knownUses) != 0 ||
            !Enum.IsDefined(approval.Status) ||
            approval.Status == MarketCycleSourceApprovalStatus.Pending)
            throw new InvalidDataException("审批状态或用途权限无效。");
        if ((approval.Status == MarketCycleSourceApprovalStatus.Approved && approval.ApprovedUses == MarketCycleSourceUse.None) ||
            ((approval.Status is MarketCycleSourceApprovalStatus.Rejected or MarketCycleSourceApprovalStatus.Revoked) &&
             approval.ApprovedUses != MarketCycleSourceUse.None))
            throw new InvalidDataException("批准记录必须明确用途；拒绝/撤销记录不能保留任何用途权限。");

        if (string.IsNullOrWhiteSpace(approval.Reviewer) || approval.Reviewer.Length > 100 ||
            approval.ReviewedAt == default || approval.ReviewedAt > now.AddSeconds(30) ||
            (approval.ReviewedAt - command.IssuedAt).Duration() > TimeSpan.FromMinutes(2) ||
            approval.ValidUntil is { } expiry && (expiry <= now || expiry <= approval.ReviewedAt))
            throw new InvalidDataException("审核人、审核时间或审批有效期无效。");
    }

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static MarketCycleSourceApproval DeserializeApproval(string json)
    {
        var approval = JsonSerializer.Deserialize<MarketCycleSourceApproval>(json);
        if (approval is null) throw new InvalidDataException("来源审批 JSON 为空或损坏。");
        return approval;
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private string ComputeAuditHash(ApprovalAuditPayload payload) =>
        Convert.ToHexString(HMACSHA256.HashData(marketCycleApprovalAuditKey,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
}
