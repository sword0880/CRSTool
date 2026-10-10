using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class HistoryPagingChecks
{
    /// <summary>超过一百份、同时间排序、越界页和复算父任务均使用隔离数据库验证。</summary>
    public static void Run(string root)
    {
        var store = new LocalStore(Path.Combine(root, "paged-history"));
        for (var i = 0; i < 105; i++) store.Save(Result($"H{i:D3}"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(store.DirectoryPath, "crs.db") }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calculations SET created_utc='2025-01-01T00:00:00Z'"; command.ExecuteNonQuery();
        }
        var first = store.QueryHistory(1, 50); var second = store.QueryHistory(2, 50); var third = store.QueryHistory(3, 50);
        var all = first.Rows.Concat(second.Rows).Concat(third.Rows).ToArray();
        if (first.TotalCount != 105 || first.Rows.Count != 50 || second.Rows.Count != 50 || third.Rows.Count != 5
            || all.Select(r => r.Id).Distinct().Count() != 105 || all[0].Id != "H104" || all[^1].Id != "H000"
            || !first.Rows.SequenceEqual(store.QueryHistory(1, 50).Rows))
            throw new Exception("历史分页丢失、重复或同时间顺序不稳定。");
        if (store.QueryHistory(999, 50).PageNumber != 3) throw new Exception("越界页未回到末页。");
        try { store.QueryHistory(0, 50); throw new Exception("无效页码未被拒绝。"); } catch (CrsException) { }
        var replay = Result("CHILD"); replay.ParentSnapshotId = "H001"; store.Save(replay);
        if (store.Load("CHILD").ParentSnapshotId != "H001" || store.QueryHistory(1, 50).Rows.First().ParentSnapshotId != "H001"
            || store.Load("H001").ParentSnapshotId is not null)
            throw new Exception("复算未保存父运行，或改动了原任务。");
        var broken = Result("BROKEN"); broken.ParentSnapshotId = "missing";
        try { store.Save(broken); throw new Exception("断链复算被保存。"); } catch (CrsException) { }
        if (store.QueryHistory(1, 50).TotalCount != 106) throw new Exception("拒绝保存仍留下半成品任务。");
        Console.WriteLine("历史持久化验证通过：超过百份分页、同秒稳定顺序、边界页、父运行及事务拒绝（4 项）。");
    }
    private static CalculationResult Result(string id)=>new() { Year=2025, Summary=new(0,0,0,0,0,0,0), SnapshotId=id };
}
