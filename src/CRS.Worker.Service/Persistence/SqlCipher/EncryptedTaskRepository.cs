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
                CREATE TABLE IF NOT EXISTS execution_progress(execution TEXT NOT NULL,sequence INTEGER NOT NULL,utc TEXT NOT NULL,detail TEXT NOT NULL,PRIMARY KEY(execution,sequence),FOREIGN KEY(execution) REFERENCES execution(id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,utc TEXT NOT NULL,sid TEXT NOT NULL,request TEXT NOT NULL,command TEXT NOT NULL,result TEXT NOT NULL);
                DROP INDEX IF EXISTS audit_request;
                CREATE TABLE IF NOT EXISTS command_request(request TEXT PRIMARY KEY,sid TEXT NOT NULL,command TEXT NOT NULL,payload TEXT NOT NULL,plugin TEXT,job TEXT,state TEXT NOT NULL,code TEXT NOT NULL,execution TEXT,created TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS one_pending_manual ON command_request(plugin,job) WHERE command='RunNow' AND state IN ('Pending','Running');
                PRAGMA application_id=1129468738; PRAGMA user_version=2;
                """);
            NormalizeStoredTimes();
            Execute("UPDATE task SET zone=$zone,revision=revision+1,reconcile=1 WHERE zone<>$zone", ("$zone", CRS.Timing.BeijingClock.ZoneId));
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
    // 提供方异常只在适配器内识别；调用方依据稳定的IO故障语义失败关闭。
    // 外层转换发生在using释放事务之后，不能改变原提交/回滚时序。
    private static T Persist<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (SqliteException error) { throw new IOException("任务仓储持久化失败。", error); }
    }
    private static void Persist(Action operation) => Persist(() => { operation(); return 0; });
    private static string Now => CRS.Timing.BeijingClock.Now.ToString("O");
    // 与启动恢复共用事务；只改时间的偏移表示，保留实际时间点和历史结果。
    private void NormalizeStoredTimes()
    {
        foreach (var (table, column) in new[] { ("execution", "scheduled"), ("execution", "started"), ("execution", "ended"),
            ("execution_progress", "utc"), ("audit", "utc"), ("task", "not_before"), ("command_request", "created") })
        {
            var rows = new List<(long Id, string Time)>();
            using (var command = Command($"SELECT rowid,{column} FROM {table} WHERE {column} IS NOT NULL AND substr({column},-6)<>'+08:00'"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetInt64(0), CRS.Timing.BeijingClock.Serialize(CRS.Timing.BeijingClock.Parse(reader.GetString(1)))));
            foreach (var row in rows) Execute($"UPDATE {table} SET {column}=$time WHERE rowid=$id", ("$time", row.Time), ("$id", row.Id));
        }
    }
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
        Persist(() =>
        {
            lock (gate) Execute("INSERT OR IGNORE INTO task VALUES($p,$j,$n,$c,$z,$e,1,1,NULL,0)",
                ("$p", plugin), ("$j", descriptor.JobId), ("$n", descriptor.Name), ("$c", descriptor.DefaultCron), ("$z", CRS.Timing.BeijingClock.ZoneId), ("$e", descriptor.EnabledByDefault));
        });
    }
    public List<TaskDefinition> Plans()
    {
        return Persist<List<TaskDefinition>>(() =>
        {
            lock (gate)
            {
                using var command = Command("SELECT * FROM task ORDER BY plugin,job"); using var reader = command.ExecuteReader();
                var list = new List<TaskDefinition>();
                while (reader.Read()) list.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetInt64(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : CRS.Timing.BeijingClock.Parse(reader.GetString(8)), reader.GetInt32(9)));
                return list;
            }
        });
    }
    public void Save(TaskDefinition plan, long expectedRevision, Guid? requestId = null)
    {
        Persist(() =>
        {
            lock (gate)
            {
                using var tx = db.BeginTransaction();
                using var command = Command("UPDATE task SET cron=$c,zone=$z,enabled=$e,revision=revision+1,reconcile=1 WHERE plugin=$p AND job=$j AND revision=$r",
                    ("$c", plan.Cron), ("$z", CRS.Timing.BeijingClock.ZoneId), ("$e", plan.Enabled), ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", expectedRevision));
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("计划已改变，请重新读取后编辑。");
                if (!plan.Enabled) CancelPending(plan.PluginId, plan.JobId);
                if (requestId is { } request) CompleteCommand(request, "Ok");
                tx.Commit();
            }
        });
    }
    public void Reconciled(TaskDefinition plan)
    {
        Persist(() =>
        {
            lock (gate) Execute("UPDATE task SET reconcile=0 WHERE plugin=$p AND job=$j AND revision=$r", ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", plan.Revision));
        });
    }
    public string Begin(TaskDefinition plan, DateTimeOffset scheduled, Guid? requestId = null)
    {
        return Persist<string>(() =>
        {
            lock (gate)
            {
                using var tx = db.BeginTransaction();
                var id = Guid.NewGuid().ToString("N");
                Execute("INSERT INTO execution VALUES($id,$p,$j,$scheduled,$now,NULL,'Running',NULL,$a)", ("$id", id), ("$p", plan.PluginId), ("$j", plan.JobId), ("$scheduled", CRS.Timing.BeijingClock.Serialize(scheduled)), ("$now", Now), ("$a", plan.Failures + 1));
                if (requestId is { } request)
                {
                    using var claim = Command("UPDATE command_request SET state='Running',execution=$id WHERE request=$r AND state='Pending'", ("$id", id), ("$r", request.ToString("N")));
                    if (claim.ExecuteNonQuery() != 1) throw new InvalidOperationException("待执行命令已取消或执行。");
                }
                tx.Commit();
                return id;
            }
        });
    }
    public void End(string id, TaskDefinition plan, BackgroundJobResult result, Guid? requestId = null)
    {
        Persist(() =>
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
                Execute("UPDATE task SET failures=$f,not_before=$n WHERE plugin=$p AND job=$j", ("$f", failed ? Math.Min(plan.Failures + 1, 100) : result.Status == "Succeeded" ? 0 : plan.Failures), ("$n", deferred ? CRS.Timing.BeijingClock.Now.AddSeconds(seconds).ToString("O") : null), ("$p", plan.PluginId), ("$j", plan.JobId));
                // 延后重试仍是同一持久命令；保留原参数，不能退化为无参数的自动任务。
                if (requestId is { } request)
                {
                    if(result.Status=="RetryDeferred")Execute("UPDATE command_request SET state='Pending',code='RetryDeferred',execution=NULL WHERE request=$r",("$r",request.ToString("N")));
                    else CompleteCommand(request, result.Status);
                }
                // 高频诊断心跳单独保留100条，不能消耗500条业务历史额度；运行中执行不参与清理。
                Execute("DELETE FROM execution WHERE result<>'Running' AND ((plugin='crs.heartbeat' AND id NOT IN (SELECT id FROM execution WHERE plugin='crs.heartbeat' ORDER BY julianday(started) DESC LIMIT 100)) OR (plugin<>'crs.heartbeat' AND id NOT IN (SELECT id FROM execution WHERE plugin<>'crs.heartbeat' ORDER BY julianday(started) DESC LIMIT 500)))");
                tx.Commit();
            }
        });
    }
    public List<ExecutionRecord> History()
    {
        return Persist<List<ExecutionRecord>>(() =>
        {
            lock (gate)
            {
                using var command = Command("SELECT * FROM execution ORDER BY julianday(started) DESC LIMIT 100"); using var reader = command.ExecuteReader();
                var rows = new List<ExecutionRecord>();
                while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), CRS.Timing.BeijingClock.Parse(reader.GetString(3)), CRS.Timing.BeijingClock.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : CRS.Timing.BeijingClock.Parse(reader.GetString(5)), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8)));
                return rows;
            }
        });
    }
    /// <summary>按稳定执行标识查询单条持久记录，不受最近历史分页或新任务插入影响。</summary>
    public ExecutionRecord? FindExecution(string id)
    {
        return Persist<ExecutionRecord?>(() =>
        {
            lock(gate)
            {
                using var command=Command("SELECT * FROM execution WHERE id=$id",("$id",id));
                using var reader=command.ExecuteReader();
                ExecutionRecord? record=reader.Read()?new(reader.GetString(0),reader.GetString(1),reader.GetString(2),CRS.Timing.BeijingClock.Parse(reader.GetString(3)),CRS.Timing.BeijingClock.Parse(reader.GetString(4)),reader.IsDBNull(5)?null:CRS.Timing.BeijingClock.Parse(reader.GetString(5)),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.GetInt32(8)):null;
                reader.Close();if(record is null)return null;
                // 单条IPC只取最近128阶段，控制消息大小；序号可明确提示更早日志未在本页返回。
                using var stages=Command("SELECT sequence,utc,detail FROM execution_progress WHERE execution=$id ORDER BY sequence DESC LIMIT 128",("$id",id));
                using var values=stages.ExecuteReader();var events=new List<ExecutionProgressEvent>();
                while(values.Read())events.Add(new(values.GetInt64(0),CRS.Timing.BeijingClock.Parse(values.GetString(1)),JsonSerializer.Deserialize<BackgroundProgress>(values.GetString(2))!));
                events.Reverse();return events.Count==0?record:record with{Progress=events};
            }
        });
    }
    /// <summary>写入正在执行的阶段并保留最近512条；时间和顺序由宿主生成，强杀后仍可读取。</summary>
    public void AppendProgress(string id,BackgroundProgress progress)
    {
        Persist(() =>
        {
            if(progress.Stage is not ("RangeStarted" or "DayStarted" or "SourceStarted" or "SourceParsed" or "SourceFailed" or "RequestStarted" or "ResponseReceived" or "RequestFailed" or "RequestTimedOut" or "RateWait" or "Cooldown" or "DayCommitted" or "RangeCompleted" or "ReportIndexPage" or "ReportIndexCommitted")
                || progress.Market is not (null or "SSE" or "SZSE") || progress.Received<0 || progress.Added<0 || progress.Duplicates<0 || progress.Pending<0 || progress.HttpStatus is <100 or >599)throw new ArgumentException("进度格式无效。");
            lock(gate)
            {
                using var transaction=db.BeginTransaction();
                using var active=Command("SELECT count(*) FROM execution WHERE id=$id AND result='Running'",("$id",id));
                if(Convert.ToInt64(active.ExecuteScalar())==0)return;
                Execute("INSERT INTO execution_progress SELECT $id,coalesce(max(sequence),0)+1,$utc,$detail FROM execution_progress WHERE execution=$id",("$id",id),("$utc",Now),("$detail",JsonSerializer.Serialize(progress)));
                Execute("DELETE FROM execution_progress WHERE execution=$id AND sequence<=(SELECT max(sequence)-512 FROM execution_progress WHERE execution=$id)",("$id",id));
                transaction.Commit();
            }
        });
    }
    public void Audit(string sid, Guid request, string command, string result)
    {
        Persist(() =>
        {
            lock (gate)
            {
                Execute("INSERT INTO audit(utc,sid,request,command,result) VALUES($utc,$sid,$id,$command,$result)", ("$utc", Now), ("$sid", sid), ("$id", request.ToString("N")), ("$command", command), ("$result", result));
                Execute("DELETE FROM audit WHERE id NOT IN (SELECT id FROM audit ORDER BY id DESC LIMIT 2000)");
            }
        });
    }
    public CommandReceipt? FindCommand(Guid request, string sid)
    {
        return Persist<CommandReceipt?>(() =>
        {
            lock (gate)
            {
                using var query = Command("SELECT state,code,execution FROM command_request WHERE request=$r AND sid=$s", ("$r", request.ToString("N")), ("$s", sid));
                using var row = query.ExecuteReader();
                return row.Read() ? new(request, row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2)) : null;
            }
        });
    }
    /// <summary>只供执行器读取已接受命令参数，客户端不能借此读取其他调用者的命令。</summary>
    public BackgroundRequest ReadCommand(Guid request)
    {
        return Persist<BackgroundRequest>(() =>
        {
            lock(gate)
            {
                using var query=Command("SELECT payload FROM command_request WHERE request=$r",("$r",request.ToString("N")));
                return JsonSerializer.Deserialize<BackgroundRequest>((string)(query.ExecuteScalar()??throw new InvalidDataException("执行命令缺失。")))
                    ??throw new InvalidDataException("执行命令损坏。");
            }
        });
    }
    public CommandReceipt? ReadCommandState(Guid request)=>Persist<CommandReceipt?>(()=>
    {
        lock(gate)
        {
            using var query=Command("SELECT state,code,execution FROM command_request WHERE request=$r",("$r",request.ToString("N")));
            using var row=query.ExecuteReader();
            return row.Read()?new(request,row.GetString(0),row.GetString(1),row.IsDBNull(2)?null:row.GetString(2)):null;
        }
    });
    public CommandReceipt AcceptCommand(BackgroundRequest request, string sid)
    {
        return Persist<CommandReceipt>(() =>
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
        });
    }
    public void CompleteCommand(Guid request, string code)
    {
        Persist(() =>
        {
            lock (gate) Execute("UPDATE command_request SET state='Completed',code=$c WHERE request=$r", ("$r", request.ToString("N")), ("$c", code));
        });
    }
    public List<(Guid RequestId, string PluginId, string JobId)> PendingRuns()
    {
        return Persist<List<(Guid RequestId, string PluginId, string JobId)>>(() =>
        {
            lock (gate)
            {
                using var query = Command("SELECT request,plugin,job FROM command_request WHERE command='RunNow' AND state='Pending' ORDER BY julianday(created),request");
                using var row = query.ExecuteReader(); var result = new List<(Guid, string, string)>();
                while (row.Read()) result.Add((Guid.ParseExact(row.GetString(0), "N"), row.GetString(1), row.GetString(2)));
                return result;
            }
        });
    }
    public void CancelPending(string plugin, string job)
    {
        Persist(() =>
        {
            lock (gate) Execute("UPDATE command_request SET state='Completed',code='Cancelled' WHERE plugin=$p AND job=$j AND command='RunNow' AND state='Pending'", ("$p", plugin), ("$j", job));
        });
    }
    // 插件及任务条件先于游标分页，查询值全部参数化。
    public (List<ExecutionRecord> Rows, string? NextCursor) HistoryPage(string? cursor, int pageSize,string? plugin=null,string? job=null,string? resultFilter=null)
    {
        return Persist<(List<ExecutionRecord> Rows, string? NextCursor)>(() =>
        {
            if (pageSize is < 1 or > 10 || resultFilter is not (null or "Issues") || (cursor is not null && (!long.TryParse(cursor, out var sequence) || sequence <= 0))) throw new ArgumentException("历史分页或结果筛选参数无效。");
            lock (gate)
            {
                // 结果筛选必须在分页 SQL 前执行；不得对当前可见10条做客户端筛选，避免翻页漏掉历史异常。
                using var query = Command("SELECT rowid,* FROM execution WHERE rowid<$before AND ($plugin IS NULL OR plugin=$plugin) AND ($job IS NULL OR job=$job) AND ($issues=0 OR result NOT IN ('Succeeded','NotPublished','Running','Cancelled')) ORDER BY rowid DESC LIMIT $limit", ("$before", cursor is null ? long.MaxValue : long.Parse(cursor, CultureInfo.InvariantCulture)), ("$limit", pageSize + 1),("$plugin",plugin),("$job",job),("$issues",resultFilter=="Issues"?1:0));
                using var reader = query.ExecuteReader(); var rows = new List<ExecutionRecord>(); string? last = null; var more = false;
                while (reader.Read())
                {
                    if (rows.Count == pageSize) { more = true; break; }
                    last = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
                    rows.Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), CRS.Timing.BeijingClock.Parse(reader.GetString(4)), CRS.Timing.BeijingClock.Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : CRS.Timing.BeijingClock.Parse(reader.GetString(6)), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9)));
                }
                return (rows, more ? last : null);
            }
        });
    }
    public void Snapshot(string destination)
    {
        Persist(() =>
        {
            lock (gate)
            {
                if (File.Exists(destination)) throw new IOException("备份目标已存在。");
                // SQLCipher 两端先设置独立服务密钥；Backup API 将 WAL 已提交内容一并纳入一致快照。
                using var target = Open(destination, key); db.BackupDatabase(target);
            }
        });
    }
    public void Dispose() { lock (gate) { db.Dispose(); CryptographicOperations.ZeroMemory(key); } }
}
