using System.Security.Cryptography;
using CRS.Timing;
using CRS.Worker.Abstractions;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

internal static class BeijingTimeChecks
{
    public static void Run()
    {
        var instant = new DateTimeOffset(2026, 10, 21, 16, 30, 0, TimeSpan.Zero);
        var local = BeijingClock.Read(new FixedClock(instant));
        if (local.Offset != TimeSpan.FromHours(8) || local.Day != 22 || local.Hour != 0 || local != instant)
            throw new Exception("北京时间跨日或真实时间点改变。");
        var root = Path.Combine(Path.GetTempPath(), "CRS.BeijingTime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tasks.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using var repository = new EncryptedTaskRepository(path, key);
        repository.RegisterDefault("crs.clock", new("clock", "时间验收", "0 * * * * ?", BeijingClock.ZoneId, true));
        var plan = repository.Plans().Single();
        var id = repository.Begin(plan, instant);
        repository.AppendProgress(id, new("RangeStarted"));
        repository.End(id, plan, new("RetryDeferred"));
        var record = repository.FindExecution(id)!;
        if (record.ScheduledUtc.Offset != BeijingClock.Offset || record.ScheduledUtc != instant ||
            record.StartedUtc.Offset != BeijingClock.Offset || record.EndedUtc?.Offset != BeijingClock.Offset ||
            record.Progress!.Single().Utc.Offset != BeijingClock.Offset || repository.Plans().Single().NotBefore?.Offset != BeijingClock.Offset)
            throw new Exception("执行、进度或重试记录没有直接保存北京时间。");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Password = Convert.ToHexString(key), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT scheduled || '|' || started || '|' || ended FROM execution WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (((string)command.ExecuteScalar()!).Split('|').Any(x => !x.EndsWith("+08:00", StringComparison.Ordinal)))
            throw new Exception("数据库原始字段仍含非北京时间。");
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO execution VALUES('legacy','crs.clock','clock','2026-10-21T02:00:00+00:00','2026-10-21T02:00:00+00:00',NULL,'Succeeded',NULL,1);
            INSERT INTO execution VALUES('beijing','crs.clock','clock','2026-10-21T09:00:00+08:00','2026-10-21T09:00:00+08:00',NULL,'Succeeded',NULL,1);
            """;
        command.ExecuteNonQuery();
        var history = repository.History().Where(x => x.ExecutionId is "legacy" or "beijing").ToArray();
        if (history[0].ExecutionId != "legacy" || history.Any(x => x.StartedUtc.Offset != BeijingClock.Offset))
            throw new Exception("新旧时区记录排序错误或读取未归一。");
        command.CommandText = "UPDATE task SET zone='UTC'";
        command.ExecuteNonQuery();
        db.Close();
        repository.Dispose();
        using var reopened = new EncryptedTaskRepository(path, key);
        var migrated = reopened.Plans().Single();
        if(migrated.TimeZoneId!=BeijingClock.ZoneId || migrated.Revision!=plan.Revision+1 || !migrated.NeedsReconcile || migrated.Cron!=plan.Cron)
            throw new Exception("既有任务时区没有以新修订切换北京时间，或Cron被改写。");
        db.Open();
        command.CommandText = "SELECT started FROM execution WHERE id='legacy'";
        if (!((string)command.ExecuteScalar()!).EndsWith("+08:00", StringComparison.Ordinal) || reopened.FindExecution("legacy")!.StartedUtc.Hour!=10)
            throw new Exception("既有执行时间未在事务内归一为北京时间，或实际时间点改变。");
        Console.WriteLine("PASS 北京时间：跨日、原始持久字段+08:00、执行/进度/重试及混合偏移排序。");
    }
    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
