using System.Globalization;
using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CRS.Worker.Service;

/// <summary>SQLCipher 是计划唯一权威；运行态只保存 RAMJobStore，可由此库重建。</summary>
public sealed class EncryptedTaskRepository : ITaskRepository, IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private readonly byte[] key;
    public EncryptedTaskRepository(string path, byte[] encryptionKey)
    {
        var existed = File.Exists(path);
        key = encryptionKey.ToArray();
        SQLitePCL.Batteries_V2.Init();
        try
        {
            db = Open(path, key);
            if (existed && Convert.ToInt32(Scalar("PRAGMA application_id")) != 1129468738)
                throw new InvalidDataException("数据库不属于此后台服务，拒绝改写。");
            var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            if (version > 2) throw new InvalidDataException("调度库版本过新。");
            if (!string.Equals(Scalar("PRAGMA quick_check")?.ToString(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("调度库完整性核验失败。");
            if (existed && version == 1)
            {
                // 在 Schema 迁移前取得含 WAL 的加密快照；旧程序见新版本会拒绝打开，禁止隐式降级。
                var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "backup"); Directory.CreateDirectory(backup);
                if ((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("迁移快照目录不可为链接。");
                var prefix = Path.GetFileName(path) + ".schema1-";
                if (Directory.EnumerateFiles(backup, prefix + "*.db").Count() >= 3) throw new InvalidDataException("迁移重试快照已达上限，需要人工核验。");
                var destination = Path.Combine(backup, prefix + Guid.NewGuid().ToString("N") + ".db");
                using var snapshot = Open(destination, key); db.BackupDatabase(snapshot);
            }
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            using var migration = db.BeginTransaction();
            Execute("""
                CREATE TABLE IF NOT EXISTS task(plugin TEXT NOT NULL,job TEXT NOT NULL,name TEXT NOT NULL,cron TEXT NOT NULL,zone TEXT NOT NULL,enabled INTEGER NOT NULL,revision INTEGER NOT NULL,reconcile INTEGER NOT NULL,not_before TEXT,failures INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(plugin,job));
                CREATE TABLE IF NOT EXISTS execution(id TEXT PRIMARY KEY,plugin TEXT NOT NULL,job TEXT NOT NULL,scheduled TEXT NOT NULL,started TEXT NOT NULL,ended TEXT,result TEXT NOT NULL,checkpoint TEXT,attempt INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,utc TEXT NOT NULL,sid TEXT NOT NULL,request TEXT NOT NULL,command TEXT NOT NULL,result TEXT NOT NULL);
                DROP INDEX IF EXISTS audit_request;
                CREATE TABLE IF NOT EXISTS command_request(request TEXT PRIMARY KEY,sid TEXT NOT NULL,command TEXT NOT NULL,payload TEXT NOT NULL,plugin TEXT,job TEXT,state TEXT NOT NULL,code TEXT NOT NULL,execution TEXT,created TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS one_pending_manual ON command_request(plugin,job) WHERE command='RunNow' AND state IN ('Pending','Running');
                PRAGMA application_id=1129468738; PRAGMA user_version=2;
                """);
            Execute("UPDATE execution SET ended=$now,result='Interrupted' WHERE result='Running'", ("$now", Now));
            Execute("UPDATE command_request SET state='Pending',code='Accepted' WHERE state='Running' AND command='RunNow'");
            migration.Commit();
        }
        catch { db?.Dispose(); CryptographicOperations.ZeroMemory(key); throw; }
    }
    private static SqliteConnection Open(string path, byte[] key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Password = Convert.ToHexString(key), Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cipher_version";
            if (string.IsNullOrWhiteSpace(command.ExecuteScalar()?.ToString())) throw new InvalidOperationException("必须使用 SQLCipher 原生提供器。");
            command.CommandText = "SELECT count(*) FROM sqlite_master"; command.ExecuteScalar();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, params (string, object?)[] args) { using var command = Command(sql, args); command.ExecuteNonQuery(); }
    private object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
    public void RegisterDefault(string plugin, BackgroundJobDescriptor descriptor)
    {
        lock (gate) Execute("INSERT OR IGNORE INTO task VALUES($p,$j,$n,$c,$z,$e,1,1,NULL,0)",
            ("$p", plugin), ("$j", descriptor.JobId), ("$n", descriptor.Name), ("$c", descriptor.DefaultCron), ("$z", descriptor.TimeZoneId), ("$e", descriptor.EnabledByDefault));
    }
    public List<TaskDefinition> Plans()
    {
        lock (gate)
        {
            using var command = Command("SELECT * FROM task ORDER BY plugin,job"); using var reader = command.ExecuteReader();
            var list = new List<TaskDefinition>();
            while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetInt64(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), reader.GetInt32(9)));
            return list;
        }
    }
    public void Save(TaskDefinition plan, long expectedRevision, Guid? requestId = null)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            using var command = Command("UPDATE task SET cron=$c,zone=$z,enabled=$e,revision=revision+1,reconcile=1 WHERE plugin=$p AND job=$j AND revision=$r",
                ("$c", plan.Cron), ("$z", plan.TimeZoneId), ("$e", plan.Enabled), ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", expectedRevision));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("计划已改变，请重新读取后编辑。");
            if (!plan.Enabled) CancelPending(plan.PluginId, plan.JobId);
            if (requestId is { } request) CompleteCommand(request, "Ok");
            tx.Commit();
        }
    }
    public void Reconciled(TaskDefinition plan)
    {
        lock (gate) Execute("UPDATE task SET reconcile=0 WHERE plugin=$p AND job=$j AND revision=$r", ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", plan.Revision));
    }
    public string Begin(TaskDefinition plan, DateTimeOffset scheduled, Guid? requestId = null)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var id = Guid.NewGuid().ToString("N");
            Execute("INSERT INTO execution VALUES($id,$p,$j,$scheduled,$now,NULL,'Running',NULL,$a)", ("$id", id), ("$p", plan.PluginId), ("$j", plan.JobId), ("$scheduled", scheduled.ToString("O")), ("$now", Now), ("$a", plan.Failures + 1));
            if (requestId is { } request)
            {
                using var claim = Command("UPDATE command_request SET state='Running',execution=$id WHERE request=$r AND state='Pending'", ("$id", id), ("$r", request.ToString("N")));
                if (claim.ExecuteNonQuery() != 1) throw new InvalidOperationException("待执行命令已取消或执行。");
            }
            tx.Commit();
            return id;
        }
    }
    public void End(string id, TaskDefinition plan, BackgroundJobResult result, Guid? requestId = null)
    {
        lock (gate)
        {
            // 执行结果和退避一次提交；进程中断后不会丢失已接受的失败分类。
            using var tx = db.BeginTransaction();
            Execute("UPDATE execution SET ended=$now,result=$result,checkpoint=$checkpoint WHERE id=$id", ("$now", Now), ("$result", result.Status), ("$checkpoint", result.Checkpoint), ("$id", id));
            var failed = result.Status is "Failed" or "AccessBlocked" or "SourceSchemaChanged";
            var deferred = failed || result.Status is "NotPublished" or "RetryDeferred";
            var seconds = Math.Min(21600, 60 * Math.Pow(2, Math.Min(plan.Failures, 8)));
            // 权限/结构变化需要长时间冷却，绝不立即反复重试受限站点。
            if (result.Status is "AccessBlocked" or "SourceSchemaChanged") seconds = 86400;
            if (!failed) seconds = 300;
            if (result.RetryAfter is { } retry) seconds = Math.Clamp(retry.TotalSeconds, failed ? seconds : 30, 86400);
            Execute("UPDATE task SET failures=$f,not_before=$n WHERE plugin=$p AND job=$j", ("$f", failed ? Math.Min(plan.Failures + 1, 100) : result.Status == "Succeeded" ? 0 : plan.Failures), ("$n", deferred ? DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("O") : null), ("$p", plan.PluginId), ("$j", plan.JobId));
            if (requestId is { } request) CompleteCommand(request, result.Status);
            Execute("DELETE FROM execution WHERE result<>'Running' AND id NOT IN (SELECT id FROM execution ORDER BY started DESC LIMIT 500)");
            tx.Commit();
        }
    }
    public List<ExecutionRecord> History()
    {
        lock (gate)
        {
            using var command = Command("SELECT * FROM execution ORDER BY started DESC LIMIT 100"); using var reader = command.ExecuteReader();
            var rows = new List<ExecutionRecord>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
            return rows;
        }
    }
    public void Audit(string sid, Guid request, string command, string result)
    {
        lock (gate)
        {
            Execute("INSERT INTO audit(utc,sid,request,command,result) VALUES($utc,$sid,$id,$command,$result)", ("$utc", Now), ("$sid", sid), ("$id", request.ToString("N")), ("$command", command), ("$result", result));
            Execute("DELETE FROM audit WHERE id NOT IN (SELECT id FROM audit ORDER BY id DESC LIMIT 2000)");
        }
    }
    public CommandReceipt? FindCommand(Guid request, string sid)
    {
        lock (gate)
        {
            using var query = Command("SELECT state,code,execution FROM command_request WHERE request=$r AND sid=$s", ("$r", request.ToString("N")), ("$s", sid));
            using var row = query.ExecuteReader();
            return row.Read() ? new(request, row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2)) : null;
        }
    }
    public CommandReceipt AcceptCommand(BackgroundRequest request, string sid)
    {
        lock (gate)
        {
            var payload = JsonSerializer.Serialize(request);
            using (var query = Command("SELECT sid,payload FROM command_request WHERE request=$r", ("$r", request.RequestId.ToString("N"))))
            using (var row = query.ExecuteReader())
                if (row.Read())
                {
                    if (row.GetString(0) != sid || row.GetString(1) != payload) throw new ArgumentException("请求标识已用于不同调用者或参数。");
                    return FindCommand(request.RequestId, sid)!;
                }
            if (Convert.ToInt64(Scalar("SELECT count(*) FROM command_request")) >= 100000) throw new InvalidOperationException("命令记录容量已满，需要维护归档。");
            var state = "Pending"; var code = request.Command == "RunNow" ? "Accepted" : "Processing";
            if (request.Command == "RunNow")
            {
                using var active = Command("SELECT count(*) FROM command_request WHERE plugin=$p AND job=$j AND command='RunNow' AND state IN ('Pending','Running')", ("$p", request.PluginId), ("$j", request.JobId));
                if (Convert.ToInt64(active.ExecuteScalar()) > 0) { state = "Completed"; code = "AlreadyRunning"; }
            }
            Execute("INSERT INTO command_request VALUES($r,$s,$c,$payload,$p,$j,$state,$code,NULL,$now)", ("$r", request.RequestId.ToString("N")), ("$s", sid), ("$c", request.Command), ("$payload", payload), ("$p", request.PluginId), ("$j", request.JobId), ("$state", state), ("$code", code), ("$now", Now));
            return new(request.RequestId, state, code, null);
        }
    }
    public void CompleteCommand(Guid request, string code)
    {
        lock (gate) Execute("UPDATE command_request SET state='Completed',code=$c WHERE request=$r", ("$r", request.ToString("N")), ("$c", code));
    }
    public List<(Guid RequestId, string PluginId, string JobId)> PendingRuns()
    {
        lock (gate)
        {
            using var query = Command("SELECT request,plugin,job FROM command_request WHERE command='RunNow' AND state='Pending' ORDER BY created,request");
            using var row = query.ExecuteReader(); var result = new List<(Guid, string, string)>();
            while (row.Read()) result.Add((Guid.ParseExact(row.GetString(0), "N"), row.GetString(1), row.GetString(2)));
            return result;
        }
    }
    public void CancelPending(string plugin, string job)
    {
        lock (gate) Execute("UPDATE command_request SET state='Completed',code='Cancelled' WHERE plugin=$p AND job=$j AND command='RunNow' AND state='Pending'", ("$p", plugin), ("$j", job));
    }
    public (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? cursor, int pageSize)
    {
        if (pageSize is < 1 or > 10 || (cursor is not null && (!long.TryParse(cursor, out var sequence) || sequence <= 0))) throw new ArgumentException("历史分页参数无效。");
        lock (gate)
        {
            using var query = Command("SELECT rowid,* FROM execution WHERE rowid<$before ORDER BY rowid DESC LIMIT $limit", ("$before", cursor is null ? long.MaxValue : long.Parse(cursor, CultureInfo.InvariantCulture)), ("$limit", pageSize + 1));
            using var reader = query.ExecuteReader(); var rows = new List<ExecutionRecord>(); string? last = null; var more = false;
            while (reader.Read())
            {
                if (rows.Count == pageSize) { more = true; break; }
                last = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
                rows.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9)));
            }
            return (rows, more ? last : null);
        }
    }
    public void Snapshot(string destination)
    {
        lock (gate)
        {
            if (File.Exists(destination)) throw new IOException("备份目标已存在。");
            // SQLCipher 两端先设置独立服务密钥；Backup API 将 WAL 已提交内容一并纳入一致快照。
            using var target = Open(destination, key); db.BackupDatabase(target);
        }
    }
    public void Dispose() { lock (gate) { db.Dispose(); CryptographicOperations.ZeroMemory(key); } }
}
