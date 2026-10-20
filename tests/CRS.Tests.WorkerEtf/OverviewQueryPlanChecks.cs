using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class OverviewQueryPlanChecks
{
    public static void Run()
    {
        void Check(bool condition,string label){if(!condition)throw new Exception(label);Console.WriteLine("PASS 概览索引："+label);}
        var root=Path.Combine(Path.GetTempPath(),"CRS.OverviewPlan",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var bytes=Encoding.UTF8.GetBytes("synthetic calendar evidence");var hash=raw.Save(bytes);string fetch;
        using(var repo=new MarketRepository(Path.Combine(root,"data"),raw,encryptionKey:key))fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-test");
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"data","market.db"),Password=Convert.ToHexString(key),Pooling=false}.ToString()))
        {
            db.Open();
            void Exec(string sql){using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
            Exec("DROP INDEX ix_quality_evidence_rule_status");
            using(var transaction=db.BeginTransaction())
            {
                using var command=db.CreateCommand();command.Transaction=transaction;
                command.CommandText="INSERT INTO quality_issue VALUES($id,'Q15','1','synthetic','Critical','Open',$id,NULL,NULL)";command.Parameters.AddWithValue("$id","");
                for(var i=0;i<20000;i++){command.Parameters["$id"].Value="unrelated-"+i;command.ExecuteNonQuery();}
                command.CommandText="INSERT INTO trading_day VALUES('SSE',$id,'"+fetch+"')";
                for(var i=0;i<260;i++){command.Parameters["$id"].Value=new DateOnly(2025,1,1).AddDays(i).ToString("yyyy-MM-dd");command.ExecuteNonQuery();}
                transaction.Commit();
            }
            const string query="EXPLAIN QUERY PLAN SELECT t.trade_date,NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=t.fetch_id AND q.rule_id='Q15' AND q.status='Open') FROM trading_day t WHERE t.market='SSE'";
            string Plan(){using var command=db.CreateCommand();command.CommandText=query;using var reader=command.ExecuteReader();var lines=new List<string>();while(reader.Read())lines.Add(reader.GetString(3));return string.Join(";",lines);}
            Check(Plan().Contains("SCAN q",StringComparison.Ordinal),"旧结构确实对每个交易日扫描质量问题表");
            string[] ReadCalendar(){using var command=db.CreateCommand();command.CommandText=query.Replace("EXPLAIN QUERY PLAN ","");using var reader=command.ExecuteReader();var rows=new List<string>();while(reader.Read())rows.Add(reader.GetString(0)+":"+reader.GetInt32(1));return rows.ToArray();}
            var timer=Stopwatch.StartNew();var before=ReadCalendar();var beforeMs=timer.ElapsedMilliseconds;
            Exec("CREATE INDEX ix_quality_evidence_rule_status ON quality_issue(evidence_id,rule_id,status)");
            Check(Plan().Contains("USING COVERING INDEX ix_quality_evidence_rule_status",StringComparison.Ordinal),"新结构使用覆盖索引，不进行逐日全表扫描");
            timer.Restart();var after=ReadCalendar();var afterMs=timer.ElapsedMilliseconds;
            Check(before.SequenceEqual(after),"加索引前后日期及完整性结论完全一致");
            Console.WriteLine(JsonSerializer.Serialize(new{QualityRows=20000,CalendarRows=260,BeforeMs=beforeMs,AfterMs=afterMs}));
        }
        using(var repo=new MarketRepository(Path.Combine(root,"data"),raw,encryptionKey:key))
        {
            var first=JsonSerializer.Serialize(repo.ExecuteOperation(repo.Overview,default));
            File.WriteAllText(Path.Combine(root,"raw",hash+".raw"),"damaged synthetic evidence");
            try{repo.ExecuteOperation(repo.Overview,default);throw new Exception("损坏原件被接受");}catch(InvalidDataException){Check(true,"索引优化后仍拒绝新损坏原件");}
            File.WriteAllBytes(Path.Combine(root,"raw",hash+".raw"),bytes);
            Check(JsonSerializer.Serialize(repo.ExecuteOperation(repo.Overview,default))==first,"重新读取修复原件，未跨查询缓存校验结论");
        }
        CryptographicOperations.ZeroMemory(key);
    }
}
