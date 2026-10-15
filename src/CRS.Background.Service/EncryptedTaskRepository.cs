using System.Globalization;
using System.Security.Cryptography;
using CRS.Background.Abstractions;
using Microsoft.Data.Sqlite;

namespace CRS.Background.Service;

/// <summary>SQLCipher 是计划唯一权威；运行态只保存 RAMJobStore，可由此库重建。</summary>
public sealed class EncryptedTaskRepository : IDisposable
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
            if (Convert.ToInt32(Scalar("PRAGMA user_version")) > 1) throw new InvalidDataException("调度库版本过新。");
            Execute("""
                PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS task(plugin TEXT NOT NULL,job TEXT NOT NULL,name TEXT NOT NULL,cron TEXT NOT NULL,zone TEXT NOT NULL,enabled INTEGER NOT NULL,revision INTEGER NOT NULL,reconcile INTEGER NOT NULL,not_before TEXT,failures INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(plugin,job));
                CREATE TABLE IF NOT EXISTS execution(id TEXT PRIMARY KEY,plugin TEXT NOT NULL,job TEXT NOT NULL,scheduled TEXT NOT NULL,started TEXT NOT NULL,ended TEXT,result TEXT NOT NULL,checkpoint TEXT,attempt INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,utc TEXT NOT NULL,sid TEXT NOT NULL,request TEXT NOT NULL,command TEXT NOT NULL,result TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS audit_request ON audit(request);
                PRAGMA application_id=1129468738; PRAGMA user_version=1;
                """);
            Execute("UPDATE execution SET ended=$now,result='Interrupted' WHERE result='Running'", ("$now", Now));
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
    public void Save(TaskDefinition plan, long expectedRevision)
    {
        lock (gate)
        {
            using var command = Command("UPDATE task SET cron=$c,zone=$z,enabled=$e,revision=revision+1,reconcile=1 WHERE plugin=$p AND job=$j AND revision=$r",
                ("$c", plan.Cron), ("$z", plan.TimeZoneId), ("$e", plan.Enabled), ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", expectedRevision));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("计划已改变，请重新读取后编辑。");
        }
    }
    public void Reconciled(TaskDefinition plan)
    {
        lock (gate) Execute("UPDATE task SET reconcile=0 WHERE plugin=$p AND job=$j AND revision=$r", ("$p", plan.PluginId), ("$j", plan.JobId), ("$r", plan.Revision));
    }
    public string Begin(TaskDefinition plan, DateTimeOffset scheduled)
    {
        lock (gate)
        {
            var id = Guid.NewGuid().ToString("N");
            Execute("INSERT INTO execution VALUES($id,$p,$j,$scheduled,$now,NULL,'Running',NULL,$a)", ("$id", id), ("$p", plan.PluginId), ("$j", plan.JobId), ("$scheduled", scheduled.ToString("O")), ("$now", Now), ("$a", plan.Failures + 1));
            return id;
        }
    }
    public void End(string id, TaskDefinition plan, BackgroundJobResult result)
    {
        lock (gate)
        {
            // 执行结果和退避一次提交；进程中断后不会丢失已接受的失败分类。
            using var tx = db.BeginTransaction();
            Execute("UPDATE execution SET ended=$now,result=$result,checkpoint=$checkpoint WHERE id=$id", ("$now", Now), ("$result", result.Status), ("$checkpoint", result.Checkpoint), ("$id", id));
            var failed = result.Status is not ("Succeeded" or "Cancelled");
            var seconds = Math.Min(21600, 60 * Math.Pow(2, Math.Min(plan.Failures, 8)));
            // 权限/结构变化需要长时间冷却，绝不立即反复重试受限站点。
            if (result.Status is "AccessBlocked" or "SourceSchemaChanged") seconds = 86400;
            Execute("UPDATE task SET failures=$f,not_before=$n WHERE plugin=$p AND job=$j", ("$f", failed ? Math.Min(plan.Failures + 1, 100) : 0), ("$n", failed ? DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("O") : null), ("$p", plan.PluginId), ("$j", plan.JobId));
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
            Execute("INSERT INTO audit(utc,sid,request,command,result) VALUES($utc,$sid,$id,$command,$result) ON CONFLICT(request) DO UPDATE SET result=$result", ("$utc", Now), ("$sid", sid), ("$id", request.ToString("N")), ("$command", command), ("$result", result));
            Execute("DELETE FROM audit WHERE id NOT IN (SELECT id FROM audit ORDER BY id DESC LIMIT 2000)");
        }
    }
    public bool ClaimCommand(string sid, Guid request, string command)
    {
        lock (gate)
        {
            using var statement = Command("INSERT OR IGNORE INTO audit(utc,sid,request,command,result) VALUES($utc,$sid,$id,$command,'Pending')", ("$utc", Now), ("$sid", sid), ("$id", request.ToString("N")), ("$command", command));
            return statement.ExecuteNonQuery() == 1;
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
