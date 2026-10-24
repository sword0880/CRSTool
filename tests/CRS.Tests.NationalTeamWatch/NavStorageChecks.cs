using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class NavStorageChecks
{
    private static readonly EtfKey Key = new("SZSE", "159915");
    private static readonly DateOnly From = new(2026, 10, 1), Through = new(2026, 10, 10);
    private sealed class Clock : TimeProvider { public DateTimeOffset At = new(2026, 10, 24, 12, 2, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => At; public void Next() => At = At.AddSeconds(1); }
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS NAV存储：" + label); }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception("未拒绝：" + label); }
    internal static CapturedNavDocument[] Corrected()
    {
        var docs = NavResearchChecks.Fixture();
        foreach (var i in new[] { 0, 2 })
        {
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(docs[i].Content).Replace("1.0900", "1.0901", StringComparison.Ordinal));
            docs[i] = docs[i] with { Content = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        return docs;
    }
    public static void Run(string root)
    {
        var clock = new Clock(); var rawRoot = Path.Combine(root, "raw"); var raw = new RawEvidenceStore(rawRoot); var data = Path.Combine(root, "data");
        string correctedId; DateTimeOffset initialAt, conflictAt; var importCommand = Guid.NewGuid();
        using (var repository = TestMarketData.Open(data, raw, timeProvider: clock))
        {
            Check(repository.NavPoints(Key, From, Through, clock.At).Count == 0, "新库不制造NAV事实或零值");
            var command = importCommand; var initial = repository.ImportNavDocuments(NavResearchChecks.Fixture(), command); initialAt = clock.At;
            var points = repository.NavPoints(Key, From, Through, clock.At);
            Check(initial is { Added: 10, Duplicates: 0, Pending: 0 } && points.Count == 10 && points[^1].UnitNavYuan == 1.09m, "首批原件核验后登记独立单位净值事实");
            Check(points.All(x => x.FirstSeenAt == initialAt && x.AcceptedAt == initialAt) && initial.RegisteredAt == initialAt, "正式首见及接受时间为实际登记时刻，不倒填下载日或净值日期");
            Check(repository.NavPoints(Key, From, Through, initialAt.AddTicks(-1)).Count == 0, "精确知识截止早一拍时看不到后来回填");
            clock.Next();
            Check(repository.ImportNavDocuments(NavResearchChecks.Fixture(), command) == initial, "稳定命令重放读取原回执，不追加版本或移动首见");
            Check(repository.ImportNavDocuments(NavResearchChecks.Fixture()) is { Added: 0, Duplicates: 10 } && repository.NavPoints(Key, From, Through, clock.At)[0].FirstSeenAt == initialAt, "同原件新请求只计重复，首次时点不变");
            Reject(() => repository.ImportNavDocuments(Corrected(), command), "原命令ID不能替换原件负载");
            clock.Next(); conflictAt = clock.At;
            Check(repository.ImportNavDocuments(Corrected()) is { Added: 1, Duplicates: 9, Pending: 1 }, "更正数值追加待审修订，其他日期不重复");
            var revisions = repository.NavRevisions(Key, From, Through, clock.At); correctedId = revisions.Single(x => x.State == "NeedsReview").Id;
            Check(repository.NavPoints(Key, From, Through, clock.At).Count == 9, "待审冲突阻断该日，不能退回旧值绕过");
            Check(repository.NavPoints(Key, From, Through, conflictAt.AddTicks(-1)).Count == 10, "当前冲突不会改写冲突前的知识视图");
            clock.Next(); repository.ReviewNavRevision(correctedId, false, "fixture-reviewer");
            Check(repository.NavPoints(Key, From, Through, clock.At)[^1].UnitNavYuan == 1.09m, "拒绝更正后保留原接受版本");
            Check(repository.NavPoints(Key, From, Through, conflictAt).Count == 9, "拒绝决定不能穿越到先前待审时点");
            clock.Next(); var reviewCommand = Guid.NewGuid(); repository.ReviewNavRevision(correctedId, true, "fixture-reviewer", reviewCommand);
            points = repository.NavPoints(Key, From, Through, clock.At);
            Check(points.Count == 10 && points[^1].UnitNavYuan == 1.0901m && points[^1].AcceptedAt == clock.At && points[^1].FirstSeenAt == conflictAt, "明确重审新值后旧版变为Superseded，首见和审核时点分别保留");
            repository.ReviewNavRevision(correctedId, true, "fixture-reviewer", reviewCommand);
            Reject(() => repository.ReviewNavRevision(correctedId, false, "fixture-reviewer", reviewCommand), "审核命令重放不能改接受为拒绝");
            Reject(() => repository.ImportNavDocuments(NavResearchChecks.Fixture(), reviewCommand), "同命令ID不能跨导入和审核业务");
            Check(((IMarketReadSessions)repository).Read(r => r.NavPoints(Key, From, Through, clock.At).Count, default) == 10, "独立只读会话读取同一正式NAV事实");
            try { ((IMarketReadSessions)repository).Read(r => r.ImportNavDocuments(NavResearchChecks.Fixture()), default); throw new Exception("只读会话写入"); }
            catch (InvalidOperationException) { Check(true, "只读会话拒绝NAV导入"); }
            var latest = clock.At; clock.At = initialAt;
            Reject(() => repository.ReviewNavRevision(correctedId, true, "fixture-reviewer"), "系统时间倒退拒绝回写知识时间线"); clock.At = latest;
            clock.Next(); repository.ImportNavDocuments(Corrected(), Guid.NewGuid()); var latestCommand = clock.At; clock.At = latest;
            Reject(() => repository.ReviewNavRevision(correctedId, true, "fixture-reviewer"), "只有重复命令也保留时间水位，不能以没有新事实绕过倒退检查"); clock.At = latestCommand;
            var display = NavResearchChecks.Fixture()[3]; var path = Path.Combine(rawRoot, display.Sha256 + ".raw"); File.WriteAllText(path, "damaged");
            Reject(() => repository.NavPoints(Key, From, Through, clock.At), "显示映射原件损坏也阻断NAV金融输入");
            Reject(() => repository.ImportNavDocuments(NavResearchChecks.Fixture()), "重复导入不能把损坏缓存报告为成功");
            Reject(() => repository.ReviewNavRevision(correctedId, true, "fixture-reviewer"), "原件损坏不能继续审核");
            repository.ReconcileEvidenceIntegrity();
            Check(repository.NavRevisions(Key, From, Through, clock.At).All(x => !x.EvidenceHealthy), "全量巡检包含NAV四类原件并保留异常状态");
            File.WriteAllBytes(path, display.Content);
            Reject(() => repository.NavPoints(Key, From, Through, clock.At), "原件恢复但Q15未复核时仍不可用");
            repository.ReconcileEvidenceIntegrity();
            Check(repository.NavPoints(Key, From, Through, clock.At).Count == 10, "复核恢复后采用正确已审版本");
            Reject(() => repository.RecordFetch(raw.Save("unverified"u8.ToArray()), EfundsNavResearch.ProductUrl), "通用来源登记没有扩大到基金网站");
            var ordinary = new EtfKey("SSE", "510300"); var fetch = repository.RecordFetch(raw.Save("shares"u8.ToArray()), "https://www.sse.com.cn/fixture.json");
            repository.ImportCalendar(fetch, [("SSE", Through)], default); repository.ImportShares(fetch, [new(ordinary, Through, 100, "份")], default);
            File.WriteAllText(path, "damaged again");
            Check(repository.Points(ordinary).Single().Shares == 100, "NAV无关损坏不拖垮仅依赖份额的读取");
            File.WriteAllBytes(path, display.Content);
        }
        using (var reopened = TestMarketData.Open(data, raw, timeProvider: clock))
        {
            Check(reopened.NavRevisions(Key, From, Through, clock.At).Count == 11 && reopened.NavPoints(Key, From, Through, clock.At)[^1].RevisionId == correctedId, "重启保留修订、审核和当前接受身份");
            Check(reopened.NavPoints(Key, From, Through, initialAt).Count == 10 && reopened.NavPoints(Key, From, Through, conflictAt).Count == 9, "重启后的历史知识视图仍可复算");
        }
        Cancellation(Path.Combine(root, "cancel"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "market.db"), Password = Convert.ToHexString(TestMarketData.Key), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM worker_business_command WHERE request=$id"; command.Parameters.AddWithValue("$id", importCommand.ToString("N")); command.ExecuteNonQuery();
        }
        using (var inconsistent = TestMarketData.Open(data, raw, timeProvider: clock))
            Reject(() => inconsistent.ImportNavDocuments(NavResearchChecks.Fixture(), importCommand), "NAV回执和业务命令缺一方时拒绝冒充完整重放");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "market.db"), Password = Convert.ToHexString(TestMarketData.Key), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE feature_schema_migration SET version=2 WHERE feature='etf-nav'"; command.ExecuteNonQuery();
        }
        Reject(() => { using var unsupported = TestMarketData.Open(data, raw, timeProvider: clock); }, "未来NAV模式版本不能静默当成当前版");
        using (var exclusive = new FileStream(Path.Combine(data, "market.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            Check(exclusive.Length > 0, "初始化失败释放数据库句柄，不遗留升级锁");
    }
    private static void Cancellation(string root)
    {
        using var cancel = new CancellationTokenSource(); var armed = false; var writes = 0;
        var raw = new RawEvidenceStore(Path.Combine(root, "raw"), () => { if (armed && ++writes == 2) cancel.Cancel(); }); var clock = new Clock();
        using var repository = TestMarketData.Open(Path.Combine(root, "data"), raw, timeProvider: clock); var id = Guid.NewGuid(); armed = true;
        try { repository.ExecuteOperation(() => repository.ImportNavDocuments(NavResearchChecks.Fixture(), id), cancel.Token); throw new Exception("忽略取消"); }
        catch (OperationCanceledException) { Check(true, "原件中途取消传递到导入事务"); }
        Check(repository.NavRevisions(Key, From, Through, clock.At).Count == 0, "取消不留下部分金融事实或命令成功回执");
        armed = false;
        Check(repository.ImportNavDocuments(NavResearchChecks.Fixture(), id).Added == 10, "取消后同一请求可重试，令牌不污染下一操作");
    }
}
