using System.IO;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>目录事务及证据负例均使用独立普通SQLite；不读正式库、不批准具名持仓。</summary>
internal static class EtfReportStorageChecks
{
    private static readonly EtfKey Key=new("SSE","510300");
    private static readonly DateOnly From=new(2026,1,1),Through=new(2026,10,27);
    private static int passed;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS ETF目录存储："+message);}
    private static void Reject<T>(Action action,string message) where T:Exception
    {try{action();}catch(T){Check(true,message);return;}throw new Exception("应当拒绝："+message);}
    private static void Sql(string data,string sql)
    {using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    internal static void Run(string root)
    {
        passed=0;var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var data=Path.Combine(root,"read-only");
        using(var repo=TestMarketData.Open(data,raw))
        {
            var bytes=EtfReportIndexChecks.SsePage();repo.SaveEtfReportIndex(Key,From,Through,[bytes]);var id=repo.EtfReportIndex(Key).SearchId;
            Reject<InvalidOperationException>(()=>repo.Read(r=>{r.SaveEtfReportIndex(Key,From,Through,[bytes]);return true;},default),"独立只读会话拒绝目录写入");
            Check(repo.EtfReportIndex(Key).SearchId==id,"拒绝只读写入后查询身份不变");
            var large=Enumerable.Range(1,10).Select(page=>System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(EtfReportIndexChecks.SsePage(250,page)).Replace("合成2026年中期报告",new string('研',900),StringComparison.Ordinal))).ToArray();
            Reject<InvalidDataException>(()=>repo.SaveEtfReportIndex(Key,From,Through,large),"提交前拒绝无法通过完整读端合同传输的大目录");
            Check(repo.EtfReportIndex(Key).SearchId==id,"超限拒绝不会覆盖旧目录或留下不可回读的成功记录");
            var hash=raw.Save(bytes);var path=Path.Combine(root,"raw",hash+".raw");File.Delete(path);
            Check(repo.Read(r=>r.EtfReportIndex(Key),default) is{State:"EvidenceUnavailable",Documents.Count:1},"原件缺失不写巡检状态，保留历史线索并撤去有效状态");File.WriteAllBytes(path,bytes);
            var changes=new[]{
                "UPDATE nt_etf_report_page SET request_body_hash='tampered'",
                "UPDATE source_fetch SET provider='OtherProvider' WHERE provider='OfficialSseEtfReportIndex'",
                "UPDATE source_fetch SET parser_version='wrong' WHERE provider='OfficialSseEtfReportIndex'",
                "UPDATE source_fetch SET status='Received' WHERE provider='OfficialSseEtfReportIndex'",
                "UPDATE source_fetch SET original_sha256='unbound' WHERE provider='OfficialSseEtfReportIndex'",
                "UPDATE source_fetch SET source_url='https://www.sse.com.cn/unbound' WHERE provider='OfficialSseEtfReportIndex'",
                "UPDATE source_fetch SET retrieved_utc='2099-01-01T00:00:00.0000000+08:00' WHERE provider='OfficialSseEtfReportIndex'"};
            for(var i=0;i<changes.Length;i++)
            {
                var damaged=Path.Combine(root,"metadata-"+i);using var target=TestMarketData.Open(damaged,raw);target.SaveEtfReportIndex(Key,From,Through,[bytes]);Sql(damaged,changes[i]);
                Check(target.EtfReportIndex(Key) is{State:"EvidenceUnavailable",Documents.Count:1},"来源请求、身份、版本、状态、哈希或时间异常阻断有效目录 "+i);
            }
        }
        var cancel=new CancellationTokenSource();var armed=false;var writes=0;
        var canceledRaw=new RawEvidenceStore(Path.Combine(root,"cancel-raw"),()=>{if(armed&&++writes==2)cancel.Cancel();});data=Path.Combine(root,"cancel");
        using(var repo=TestMarketData.Open(data,canceledRaw))
        {
            var id=Guid.NewGuid();armed=true;
            Reject<OperationCanceledException>(()=>repo.ExecuteOperation(()=>{repo.SaveEtfReportIndex(Key,From,Through,[EtfReportIndexChecks.SsePage(26),EtfReportIndexChecks.SsePage(26,2)],id);return true;},cancel.Token),"第二页写入中取消，整批目录事务回滚");
            Check(repo.EtfReportIndex(Key).State=="NotSearched"&&!repo.EtfReportCommandComplete(id,Key,From,Through),"取消不留下半批目录或成功命令");
            armed=false;repo.SaveEtfReportIndex(Key,From,Through,[EtfReportIndexChecks.SsePage(26),EtfReportIndexChecks.SsePage(26,2)],id);
            Check(repo.EtfReportIndex(Key).Documents.Count==26&&repo.EtfReportCommandComplete(id,Key,From,Through),"同一请求可在取消后完整重试，令牌不污染下一操作");
        }
        cancel.Dispose();data=Path.Combine(root,"transaction-fault");
        using(var repo=TestMarketData.Open(data,raw))
        {
            var id=Guid.NewGuid();Sql(data,"CREATE TRIGGER reject_etf_report_page BEFORE INSERT ON nt_etf_report_page WHEN NEW.page_no=2 BEGIN SELECT RAISE(ABORT,'synthetic page fault'); END;");
            Reject<IOException>(()=>repo.ExecuteOperation(()=>{repo.SaveEtfReportIndex(Key,From,Through,[EtfReportIndexChecks.SsePage(26),EtfReportIndexChecks.SsePage(26,2)],id);return true;},default),"第二页SQL故障回滚目录、来源和命令");
            Check(repo.EtfReportIndex(Key).State=="NotSearched"&&!repo.EtfReportCommandComplete(id,Key,From,Through),"SQL故障后不返回部分成功");
            Sql(data,"DROP TRIGGER reject_etf_report_page");repo.SaveEtfReportIndex(Key,From,Through,[EtfReportIndexChecks.SsePage(26),EtfReportIndexChecks.SsePage(26,2)],id);
            Check(repo.EtfReportIndex(Key).Documents.Count==26,"修复故障后原请求完整提交");
        }
        var schemas=new[]{"DROP TABLE nt_etf_report_page","UPDATE feature_schema_migration SET version=99 WHERE feature='etf-report-index'","DELETE FROM feature_schema_migration WHERE feature='etf-report-index'"};
        for(var i=0;i<schemas.Length;i++)
        {
            data=Path.Combine(root,"schema-"+i);using(var seeded=TestMarketData.Open(data,raw))seeded.SaveEtfReportIndex(Key,From,Through,[EtfReportIndexChecks.SsePage()]);
            Sql(data,schemas[i]);Reject<InvalidDataException>(()=>{using var damaged=TestMarketData.Open(data,raw);},"模式缺表、未知版本或记录遗失时拒绝自动补建 "+i);
        }
        Console.WriteLine($"ETF目录存储边界{passed}项通过，仅使用隔离普通SQLite。");
    }
}
