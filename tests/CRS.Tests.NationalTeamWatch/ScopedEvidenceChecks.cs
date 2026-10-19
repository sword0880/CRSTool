using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>用真实独占文件锁和损坏/恢复验证查询依赖范围，不用计时阈值冒充性能证据。</summary>
internal static class ScopedEvidenceChecks
{
    public static void Run(string root)
    {
        void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS 原件查询："+name);}
        var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        var key=new EtfKey("SSE","510300");var other=new EtfKey("SSE","510050");var day=new DateOnly(2026,9,1);
        var calendarHash=raw.Save("calendar fixture"u8.ToArray());var calendarFetch=repository.RecordFetch(calendarHash,"https://www.sse.com.cn/");
        repository.ImportCalendar(calendarFetch,[("SSE",day)],default);
        var content=Encoding.UTF8.GetBytes("own shares original");var hash=raw.Save(content);
        repository.ImportShares(repository.RecordFetch(hash,"https://www.sse.com.cn/"),[new(key,day,100,"份")],default);
        var otherBytes=Encoding.UTF8.GetBytes("unrelated shares original");var otherHash=raw.Save(otherBytes);
        repository.ImportShares(repository.RecordFetch(otherHash,"https://www.sse.com.cn/"),[new(other,day,200,"份")],default);
        using(var locked=new FileStream(Path.Combine(rawPath,otherHash+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            Check(repository.Points(key).Single().Shares==100,"单只份额不读取被占用的无关证券原件");
            Check(repository.Evidence(key).Single().State=="Accepted","单只证据仍校验自身事实");
            Check(repository.Acceptance(key,day).Single().AcceptedDays==1,"逐证券验收核验所属日历和份额，不读取独立证券原件");
            try{repository.Evidence();throw new Exception("全池漏检无关锁定原件");}
            catch(IOException){Check(true,"全池查询仍核验全部依赖，不借范围优化放松完整性");}
        }
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"tampered");
        Check(repository.Points(key).Count==0&&repository.Evidence(key).Single().State=="Critical","下一次查询重新校验，损坏原件不得沿用上次结果");
        Check(repository.Points(other).Single().Shares==200,"损坏证券不污染独立证券事实");
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),content);
        Check(repository.Points(key).Single().Shares==100&&repository.Evidence(key).Single().State=="Accepted","原始哈希恢复后重新核验，保留恢复链");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try{repository.ExecuteOperation(()=>repository.Points(key),cancelled.Token);throw new Exception("取消后仍返回金融结果");}
            catch(OperationCanceledException){Check(true,"同步原件核验响应查询取消，不返回半份结果");}
        }
        Check(repository.Points(key).Count==1,"取消作用域结束不污染下次正常查询");
        // 直接在同一真实连接执行长SQL，验证取消进入原生SQLite执行器，而非只在查询前检查令牌。
        var connection=(Microsoft.Data.Sqlite.SqliteConnection)typeof(MarketRepository).GetField("db",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(repository)!;
        using(var native=connection.CreateCommand())
        using(var midQuery=new CancellationTokenSource())
        {
            native.CommandText="WITH RECURSIVE long_query(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM long_query WHERE n<100000000) SELECT sum(n) FROM long_query";
            var query=Task.Run(()=>repository.ExecuteOperation(()=>{midQuery.CancelAfter(100);return native.ExecuteScalar();},midQuery.Token));
            try{query.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();throw new Exception("长SQL忽略取消");}
            catch(OperationCanceledException){Check(true,"运行中的原生SQL响应取消，并转换为取消而非存储故障");}
        }
        Check(repository.ExecuteOperation(()=>repository.Points(key),CancellationToken.None).Single().Shares==100,"SQLite中断结束后连接可继续正确查询");
        File.WriteAllText(Path.Combine(rawPath,calendarHash+".raw"),"damaged calendar");
        repository.Acceptance(key,day);
        using var issue=connection.CreateCommand();issue.CommandText="SELECT count(*) FROM quality_issue WHERE evidence_id=$id AND rule_id='Q15' AND status='Open'";issue.Parameters.AddWithValue("$id",calendarFetch);
        Check(Convert.ToInt32(issue.ExecuteScalar())==1,"逐证券验收仍检出所属市场日历原件损坏");
    }
}
