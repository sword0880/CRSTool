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
        var connection=(Microsoft.Data.Sqlite.SqliteConnection)typeof(MarketRepository).GetProperty("db",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(repository)!;
        long Changes(){using var command=connection.CreateCommand();command.CommandText="SELECT total_changes()";return Convert.ToInt64(command.ExecuteScalar());}
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
        // 未关联任何金融事实的 RawSaved 抓取不属于这些查询的依赖；独占锁可稳定证明未扫描它。
        var orphanHash=raw.Save("orphan fetch not attached to a business fact"u8.ToArray());
        repository.RecordFetch(orphanHash,"https://www.sse.com.cn/orphan-fetch");
        using(var orphanLock=new FileStream(Path.Combine(rawPath,orphanHash+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            Check(repository.Overview().Count>0,"ETF概览只核验其仪表盘证据，不被孤立抓取阻断");
            Check(repository.Calendar("SSE").Contains(day),"交易日历不被孤立抓取阻断");
            _=repository.History(null);Check(true,"全池历史不扫描未关联的孤立抓取");
            Check(repository.PointsForKeys([key,other]).Count==2,"批量份额查询不扫描未关联的孤立抓取");
            Check(repository.Evidence().Count==2,"全池证据核验份额事实，但不扫描未关联的孤立抓取");
            _=repository.Coverage(key,day,day);Check(true,"单证券覆盖不被孤立抓取阻断");
        }
        // Coverage 还会展示最新采集尝试引用的原件；该依赖必须纳入校验，不能因缩小份额范围而漏检。
        var attemptHash=raw.Save("collection attempt evidence"u8.ToArray());
        var attemptFetchId=repository.RecordFetch(attemptHash,"https://www.sse.com.cn/attempt-fetch");
        var attemptId=repository.BeginCollection(key,day);
        repository.RecordCollection(attemptId,"RawSaved","RawSaved",attemptFetchId);
        using(var attemptLock=new FileStream(Path.Combine(rawPath,attemptHash+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            try{repository.Coverage(key,day,day);throw new Exception("覆盖查询放行了已被当前采集阶段引用的锁定原件");}
            catch(Exception error) when(error is IOException or InvalidDataException)
            {Check(true,"覆盖查询继续核验最新采集尝试所引用的原件");}
        }
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"tampered");
        var beforeDamagedQuery=Changes();
        try{repository.Points(key);throw new Exception("已损坏原件被读查询放行");}
        catch(InvalidDataException){Check(true,"查询核验原件损坏时拒绝返回金融数据，且不持久化状态");}
        Check(Changes()==beforeDamagedQuery,"损坏查询前后数据库写集合不变");
        using(var fault=connection.CreateCommand())
        {
            fault.CommandText="CREATE TEMP TRIGGER fail_integrity_issue BEFORE INSERT ON quality_issue WHEN NEW.rule_id='Q15' BEGIN SELECT RAISE(ABORT,'fixture integrity failure'); END";fault.ExecuteNonQuery();
            try{repository.ReconcileEvidenceIntegrity();throw new Exception("完整性事务故障未触发。");}
            catch(Microsoft.Data.Sqlite.SqliteException error) when(error.SqliteErrorCode==19){}
            fault.CommandText="SELECT (SELECT count(*) FROM evidence_integrity_event)+(SELECT count(*) FROM quality_issue WHERE rule_id='Q15')";
            Check(Convert.ToInt32(fault.ExecuteScalar())==0,"完整性状态写入失败回滚同事务事件，不留半状态");
            fault.CommandText="DROP TRIGGER fail_integrity_issue";fault.ExecuteNonQuery();
        }
        repository.ReconcileEvidenceIntegrity();
        var afterDamage=Changes();repository.ReconcileEvidenceIntegrity();
        Check(Changes()==afterDamage,"完整性损坏巡检重复运行不写入");
        Check(repository.Points(key).Count==0 && repository.Evidence(key).Single().State=="Critical",
            "受控完整性作业登记Q15后，页面可只读显示Critical且不返回损坏份额");
        Check(repository.Points(other).Single().Shares==200,"损坏证券不污染独立证券事实");
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),content);
        var beforeRestoredQuery=Changes();repository.Evidence(key);
        Check(Changes()==beforeRestoredQuery,"原件恢复后的查询不擅自持久化恢复状态");
        repository.ReconcileEvidenceIntegrity();
        var afterRestore=Changes();repository.ReconcileEvidenceIntegrity();
        Check(Changes()==afterRestore,"恢复巡检重复运行不制造审计事件");
        Check(repository.Points(key).Single().Shares==100&&repository.Evidence(key).Single().State=="Accepted","原始哈希恢复后重新核验，保留恢复链");
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try{repository.ExecuteOperation(()=>repository.Points(key),cancelled.Token);throw new Exception("取消后仍返回金融结果");}
            catch(OperationCanceledException){Check(true,"同步原件核验响应查询取消，不返回半份结果");}
        }
        Check(repository.Points(key).Count==1,"取消作用域结束不污染下次正常查询");
        // 直接在同一真实连接执行长SQL，验证取消进入原生SQLite执行器，而非只在查询前检查令牌。
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
        repository.ReconcileEvidenceIntegrity();
        using var issue=connection.CreateCommand();issue.CommandText="SELECT count(*) FROM quality_issue WHERE evidence_id=$id AND rule_id='Q15' AND status='Open'";issue.Parameters.AddWithValue("$id",calendarFetch);
        Check(Convert.ToInt32(issue.ExecuteScalar())==1,"逐证券验收仍检出所属市场日历原件损坏");
    }
}
