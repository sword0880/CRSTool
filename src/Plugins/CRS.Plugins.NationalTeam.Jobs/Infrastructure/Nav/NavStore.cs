using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>Dedicated NAV evidence and revision store; uses the existing SQLCipher writer/read snapshot and receipts.</summary>
internal sealed class NavStore
{
    private readonly IMarketRepositorySession _session;
    public NavStore(IMarketRepositorySession session)=>_session=session;
    private static string Decimal(decimal value)=>value.ToString(CultureInfo.InvariantCulture);
    /// <summary>纯读取双回执；任一方缺损或操作不符时拒绝冒充已完成导入。</summary>
    public NavImportOutcome? NavImportReceipt(Guid commandId)
    {
        var prior = _session.Scalar("SELECT operation FROM worker_business_command WHERE request=$id", ("$id", commandId.ToString("N"))) as string;
        using var command = _session.Command("SELECT operation,result_json FROM nt_nav_command WHERE id=$id", ("$id", commandId.ToString("N"))); using var reader = command.ExecuteReader();
        if (!reader.Read()) return prior is null ? null : throw new InvalidDataException("NAV导入回执不完整或命令类型不符。");
        if (prior != "nav-import" || reader.GetString(0) != "nav-import") throw new InvalidDataException("NAV导入回执不一致。");
        return JsonSerializer.Deserialize<NavImportOutcome>(reader.GetString(1)) ?? throw new InvalidDataException("NAV导入结果损坏。");
    }
    private static string NavTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public void InitializeSchema()
    {
        var version = _session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='etf-nav'");
        if (version is not null && Convert.ToInt32(version) != 1) throw new InvalidDataException("NAV存储版本不受支持，拒绝猜测兼容。");
        // 有迁移记录但表缺失、或无记录却遗留部分表，都不能在启动时自动补建伪装为空库。
        var existingTables = Convert.ToInt32(_session.Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_nav_capture','nt_nav_capture_document','nt_nav_revision','nt_nav_revision_event','nt_nav_command')"));
        if ((version is null && existingTables != 0) || (version is not null && existingTables != 5))
            throw new InvalidDataException("NAV迁移登记与实际表结构不一致，拒绝自动修补或清空金融证据。");
        _session.BeforeWrite?.Invoke(); using var transaction = _session.Connection.BeginTransaction();
        _session.Execute("""
            CREATE TABLE IF NOT EXISTS nt_nav_capture(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,parser_version TEXT NOT NULL,verified_at TEXT NOT NULL,registered_at TEXT NOT NULL,fingerprint TEXT NOT NULL,row_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_nav_capture_document(capture_id TEXT NOT NULL REFERENCES nt_nav_capture(id),fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(capture_id,fetch_id));
            CREATE TABLE IF NOT EXISTS nt_nav_revision(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,nav_date TEXT NOT NULL,unit_nav TEXT NOT NULL,accumulated_nav TEXT NOT NULL,revision_no INTEGER NOT NULL,first_seen TEXT NOT NULL,capture_id TEXT NOT NULL REFERENCES nt_nav_capture(id),UNIQUE(market,ticker,nav_date,revision_no));
            CREATE TABLE IF NOT EXISTS nt_nav_revision_event(sequence INTEGER PRIMARY KEY AUTOINCREMENT,revision_id TEXT NOT NULL REFERENCES nt_nav_revision(id),state TEXT NOT NULL CHECK(state IN('Accepted','NeedsReview','Rejected','Superseded')),recorded_at TEXT NOT NULL,reviewer TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nt_nav_revision_day ON nt_nav_revision(market,ticker,nav_date,revision_no);
            CREATE INDEX IF NOT EXISTS ix_nt_nav_event_time ON nt_nav_revision_event(revision_id,recorded_at,sequence);
            CREATE TABLE IF NOT EXISTS nt_nav_command(id TEXT PRIMARY KEY,operation TEXT NOT NULL,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,completed_at TEXT NOT NULL);
            """);
        _session.Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('etf-nav',1,$now)", ("$now", NavTime(_session.Clock.GetUtcNow())));
        transaction.Commit();
    }

    public NavImportOutcome ImportNavDocuments(IReadOnlyList<CapturedNavDocument> documents, Guid? commandId = null)
    {
        _session.OperationCancellation.ThrowIfCancellationRequested();
        var frozen = documents.Select(x => x with { Content = x.Content.ToArray() }).ToArray();
        var dataset = EfundsNavResearch.Build(frozen, _session.Clock.GetUtcNow());
        var captureId = ExitAlertEvaluator.Hash(dataset.ParserVersion + "|" + string.Join('|', dataset.Sources.OrderBy(x => x.Url, StringComparer.Ordinal)
            .Select(x => x.Url + ":" + x.Sha256 + ":" + NavTime(x.RetrievedAt))));
        if (NavCommandReplay(commandId, "nav-import", captureId) is { } replay)
            return JsonSerializer.Deserialize<NavImportOutcome>(replay) ?? throw new InvalidDataException("NAV命令回执损坏。");
        _session.BeforeWrite?.Invoke(); using var transaction = _session.Connection.BeginTransaction(); var now = NavWriteAt();
        var registered = _session.Scalar("SELECT registered_at FROM nt_nav_capture WHERE id=$id", ("$id", captureId)) as string;
        if (registered is not null)
        {
            if (!NavCaptureHealth([captureId])[captureId]) throw new InvalidDataException("已登记NAV原件损坏，重复导入不能冒充健康缓存。");
            var duplicate = new NavImportOutcome(captureId, 0, dataset.Points.Count, 0, DateTimeOffset.Parse(registered, CultureInfo.InvariantCulture));
            RecordNavCommand(commandId, "nav-import", captureId, JsonSerializer.Serialize(duplicate), now); transaction.Commit(); return duplicate;
        }
        _session.Execute("INSERT INTO nt_nav_capture VALUES($id,$m,$t,$p,$v,$at,$h,$count)", ("$id", captureId), ("$m", dataset.Market), ("$t", dataset.Ticker),
            ("$p", dataset.ParserVersion), ("$v", NavTime(dataset.VerifiedAt)), ("$at", NavTime(now)), ("$h", dataset.Fingerprint), ("$count", dataset.Points.Count));
        foreach (var source in frozen)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested(); var hash = _session.RawEvidence.Save(source.Content); var fetch = Guid.NewGuid().ToString("N");
            // 只有上方固定四原件解析成功的内部路径能登记基金来源；不扩大通用RecordFetch或HTTP白名单。
            _session.Execute("INSERT INTO source_fetch VALUES($id,'OfficialEfundsNav',$retrieved,NULL,$url,$hash,$hash,'Accepted',$parser)",
                ("$id", fetch), ("$retrieved", NavTime(source.RetrievedAt)), ("$url", source.Url), ("$hash", hash), ("$parser", dataset.ParserVersion));
            _session.Execute("INSERT INTO nt_nav_capture_document VALUES($capture,$fetch)", ("$capture", captureId), ("$fetch", fetch));
        }
        var key = new EtfKey(dataset.Market, dataset.Ticker);
        var existing = NavRevisions(key, dataset.Points[0].Date, dataset.Points[^1].Date, now).GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToArray());
        var added = 0; var duplicates = 0; var pending = 0;
        foreach (var point in dataset.Points)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested(); var prior = existing.GetValueOrDefault(point.Date) ?? [];
            // 相同数值重复来源不重写首见/审核时间，也不能复活曾经拒绝的版本。
            if (prior.Any(x => x.UnitNavYuan == point.UnitNavYuan && x.AccumulatedNavYuan == point.AccumulatedNavYuan)) { duplicates++; continue; }
            var state = prior.Length == 0 ? "Accepted" : "NeedsReview"; var id = Guid.NewGuid().ToString("N");
            _session.Execute("INSERT INTO nt_nav_revision VALUES($id,$m,$t,$d,$unit,$acc,$revision,$at,$capture)", ("$id", id), ("$m", dataset.Market), ("$t", dataset.Ticker),
                ("$d", point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$unit", Decimal(point.UnitNavYuan)), ("$acc", Decimal(point.AccumulatedNavYuan)),
                ("$revision", prior.Length == 0 ? 1 : prior.Max(x => x.Revision) + 1), ("$at", NavTime(now)), ("$capture", captureId));
            AppendNavEvent(id, state, "official-parser:" + dataset.ParserVersion, now); added++; if (state == "NeedsReview") pending++;
        }
        var outcome = new NavImportOutcome(captureId, added, duplicates, pending, now);
        RecordNavCommand(commandId, "nav-import", captureId, JsonSerializer.Serialize(outcome), now);
        _session.OperationCancellation.ThrowIfCancellationRequested(); transaction.Commit(); return outcome;
    }

    public IReadOnlyList<NavRevision> NavRevisions(EtfKey key, DateOnly from, DateOnly through, DateTimeOffset knowledgeCutoff)
    {
        ValidateNavQuery(key, from, through, knowledgeCutoff); var rows = new List<NavRevision>();
        using (var command = _session.Command("""
            SELECT r.id,r.market,r.ticker,r.nav_date,r.unit_nav,r.accumulated_nav,r.revision_no,e.state,r.first_seen,e.recorded_at,r.capture_id
            FROM nt_nav_revision r JOIN nt_nav_revision_event e ON e.sequence=(
                SELECT max(sequence) FROM nt_nav_revision_event x WHERE x.revision_id=r.id AND x.recorded_at<=$cutoff)
            WHERE r.market=$m AND r.ticker=$t AND r.nav_date>=$from AND r.nav_date<=$through AND r.first_seen<=$cutoff
            ORDER BY r.nav_date,r.revision_no
            """, ("$cutoff", NavTime(knowledgeCutoff)), ("$m", key.Market), ("$t", key.Ticker), ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$through", through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                _session.CheckReadLimit(rows.Count + 1);
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture), decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetInt32(6), reader.GetString(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture), reader.GetString(10), false));
            }
        var health = NavCaptureHealth(rows.Select(x => x.CaptureId));
        return rows.Select(x => x with { EvidenceHealthy = health[x.CaptureId] }).ToArray();
    }

    public IReadOnlyList<NavAcceptedPoint> NavPoints(EtfKey key, DateOnly from, DateOnly through, DateTimeOffset knowledgeCutoff)
    {
        var rows = NavRevisions(key, from, through, knowledgeCutoff);
        if (rows.Any(x => (x.State is "Accepted" or "NeedsReview") && !x.EvidenceHealthy))
            throw new InvalidDataException("NAV依赖原件缺失、损坏或仍处于完整性异常，拒绝返回金融计算输入。");
        var result = new List<NavAcceptedPoint>();
        foreach (var day in rows.GroupBy(x => x.Date))
        {
            if (day.Any(x => x.State == "NeedsReview")) continue;
            var accepted = day.Where(x => x.State == "Accepted").ToArray();
            if (accepted.Length > 1) throw new InvalidDataException("同日存在多个已接受NAV版本，不能择优。");
            if (accepted.Length == 1)
            {
                var row = accepted[0]; result.Add(new(row.Date, row.UnitNavYuan, row.AccumulatedNavYuan, row.Id, row.FirstSeenAt, row.StateRecordedAt, row.CaptureId));
            }
        }
        return result;
    }

    public void ReviewNavRevision(string revisionId, bool accept, string reviewer, Guid? commandId = null)
    {
        if (string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 120 || reviewer.Any(char.IsControl)) throw new InvalidDataException("NAV审核人无效。");
        var payload = ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new { revisionId, accept, reviewer }));
        if (NavCommandReplay(commandId, "nav-review", payload) is not null) return;
        _session.BeforeWrite?.Invoke(); using var transaction = _session.Connection.BeginTransaction(); var now = NavWriteAt();
        (EtfKey Key, DateOnly Day)? target = null;
        using (var command = _session.Command("SELECT market,ticker,nav_date FROM nt_nav_revision WHERE id=$id", ("$id", revisionId)))
        using (var reader = command.ExecuteReader()) if (reader.Read()) target = (new(reader.GetString(0), reader.GetString(1)), DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (target is not { } selected) throw new InvalidDataException("NAV修订不存在。");
        var rows = NavRevisions(selected.Key, selected.Day, selected.Day, now); var row = rows.Single(x => x.Id == revisionId);
        if (!row.EvidenceHealthy) throw new InvalidDataException("NAV原件未通过完整性校验，不能审核。");
        if (accept)
            foreach (var other in rows.Where(x => x.Id != row.Id && (x.State is "Accepted" or "NeedsReview")))
                AppendNavEvent(other.Id, other.State == "Accepted" ? "Superseded" : "Rejected", reviewer, now);
        AppendNavEvent(row.Id, accept ? "Accepted" : "Rejected", reviewer, now);
        RecordNavCommand(commandId, "nav-review", payload, "{}", now);
        _session.OperationCancellation.ThrowIfCancellationRequested(); transaction.Commit();
    }

    private Dictionary<string, bool> NavCaptureHealth(IEnumerable<string> captureIds)
    {
        var captures = new Dictionary<string, List<(string Hash, string Original, string Url, bool Valid)>>(StringComparer.Ordinal);
        foreach (var id in captureIds.Distinct(StringComparer.Ordinal))
        {
            var docs = new List<(string Hash, string Original, string Url, bool Valid)>(); captures.Add(id, docs);
            using var command = _session.Command("""
                SELECT f.sha256,f.original_sha256,f.source_url,
                    f.status='Accepted' AND f.provider='OfficialEfundsNav' AND f.parser_version=c.parser_version AND f.retrieved_utc<=c.registered_at
                    AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                FROM nt_nav_capture_document d JOIN source_fetch f ON f.fetch_id=d.fetch_id JOIN nt_nav_capture c ON c.id=d.capture_id WHERE d.capture_id=$id
                """, ("$id", id));
            using var reader = command.ExecuteReader(); while (reader.Read()) docs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1));
        }
        var verified = _session.RawEvidence.VerifyMany(captures.Values.SelectMany(x => x.SelectMany(d => new[] { d.Hash, d.Original })), _session.OperationCancellation);
        var allowed = new[] { EfundsNavResearch.ProductUrl, EfundsNavResearch.ArchiveUrl, EfundsNavResearch.RecentUrl, EfundsNavResearch.DisplayUrl };
        return captures.ToDictionary(x => x.Key, x => x.Value.Count == 4 && x.Value.Select(d => d.Url).Distinct(StringComparer.Ordinal).Count() == 4
            && x.Value.All(d => d.Valid && allowed.Contains(d.Url, StringComparer.Ordinal) && verified[d.Hash] && verified[d.Original]), StringComparer.Ordinal);
    }

    private void ValidateNavQuery(EtfKey key, DateOnly from, DateOnly through, DateTimeOffset cutoff)
    {
        if (key.Market is not ("SSE" or "SZSE") || key.Ticker.Length != 6 || !key.Ticker.All(char.IsAsciiDigit) || from > through
            || through.DayNumber - from.DayNumber > 20000 || cutoff == default || cutoff > _session.Clock.GetUtcNow())
            throw new InvalidDataException("NAV证券、查询区间或知识截止时间无效。");
    }
    private DateTimeOffset NavWriteAt()
    {
        var now = _session.Clock.GetUtcNow().ToUniversalTime();
        var latest = _session.Scalar("SELECT max(at) FROM (SELECT max(registered_at) AS at FROM nt_nav_capture UNION ALL SELECT max(recorded_at) AS at FROM nt_nav_revision_event UNION ALL SELECT max(completed_at) AS at FROM nt_nav_command UNION ALL SELECT applied_at AS at FROM feature_schema_migration WHERE feature='etf-nav')") as string;
        if (latest is not null && now < DateTimeOffset.Parse(latest, CultureInfo.InvariantCulture)) throw new InvalidDataException("系统时间倒退，暂停NAV写入以保护知识时间线。");
        return now;
    }
    private void AppendNavEvent(string revision, string state, string reviewer, DateTimeOffset at) => _session.Execute(
        "INSERT INTO nt_nav_revision_event(revision_id,state,recorded_at,reviewer) VALUES($id,$state,$at,$who)", ("$id", revision), ("$state", state), ("$at", NavTime(at)), ("$who", reviewer));
    private string? NavCommandReplay(Guid? id, string operation, string payload)
    {
        if (id is not { } value) return null;
        var prior = _session.Scalar("SELECT operation FROM worker_business_command WHERE request=$id", ("$id", value.ToString("N"))) as string;
        if (prior is not null && prior != operation) throw new InvalidDataException("命令ID已用于其他业务操作。");
        using var command = _session.Command("SELECT operation,payload_hash,result_json FROM nt_nav_command WHERE id=$id", ("$id", value.ToString("N"))); using var reader = command.ExecuteReader();
        if (!reader.Read()) return prior is null ? null : throw new InvalidDataException("NAV幂等回执不完整，拒绝重放写入。");
        if (prior is null) throw new InvalidDataException("NAV回执缺少对应业务命令，拒绝不一致的重放。");
        if (reader.GetString(0) != operation || reader.GetString(1) != payload) throw new InvalidDataException("NAV命令ID不能改操作或负载。");
        return reader.GetString(2);
    }
    private void RecordNavCommand(Guid? id, string operation, string payload, string result, DateTimeOffset at)
    {
        if (id is not { } value) return;
        _session.Execute("INSERT INTO nt_nav_command VALUES($id,$operation,$payload,$result,$at)", ("$id", value.ToString("N")), ("$operation", operation), ("$payload", payload), ("$result", result), ("$at", NavTime(at)));
        _session.RecordCommand(id, operation);
    }
}
