using System.Collections.Concurrent;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>真实只读事务内的原件复用边界：计数、独占锁、取消及跨查询损坏，不依赖磁盘计时。</summary>
internal static class SnapshotVerificationChecks
{
    public static void Run(string root)
    {
        var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 快照核验："+label);}
        var reads=new ConcurrentDictionary<string,int>(StringComparer.Ordinal);string? failHash=null;
        var rawRoot=Path.Combine(root,"raw");
        var raw=new RawEvidenceStore(rawRoot,openRead:path=>
        {
            var hash=Path.GetFileNameWithoutExtension(path);reads.AddOrUpdate(hash,1,(_,count)=>count+1);
            if(hash==failHash)throw new IOException("合成原件读取中断");
            return File.OpenRead(path);
        });
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        var sessions=(IMarketReadSessions)repository;var key=new EtfKey("SSE","510300");var other=new EtfKey("SZSE","159915");var day=new DateOnly(2026,9,1);
        var calendar=raw.Save("snapshot calendar"u8.ToArray());var ownBytes="snapshot own shares"u8.ToArray();var own=raw.Save(ownBytes);
        var unrelated=raw.Save("snapshot another market"u8.ToArray());var orphan=raw.Save("snapshot orphan retained by full acceptance"u8.ToArray());
        repository.ImportCalendar(repository.RecordFetch(calendar,"https://www.sse.com.cn/snapshot-calendar"),[("SSE",day),("SZSE",day)],default);
        repository.ImportShares(repository.RecordFetch(own,"https://www.sse.com.cn/snapshot-own"),[new(key,day,100,"份")],default);
        repository.ImportShares(repository.RecordFetch(unrelated,"https://www.szse.cn/snapshot-other"),[new(other,day,200,"份")],default);
        repository.RecordFetch(orphan,"https://www.sse.com.cn/snapshot-orphan");
        reads.Clear();
        var snapshot=sessions.Read(current=>
        {
            Check(current.Acceptance(key,day).Single().AcceptedDays==1,"单只验收在快照内保留事实");
            Check(current.Points(key).Single().Shares==100,"同快照再次读取仍返回相同份额");
            return current.Acceptance(day);
        },default);
        Check(reads.Count==4&&reads.Values.All(x=>x==1),"单只、份额及全池串联只完整读验每份原件一次，新增依赖继续核验");
        Check(JsonSerializer.Serialize(snapshot)==JsonSerializer.Serialize(repository.Acceptance(day)),"复用不改变验收字段或缩小配置分母");
        reads.Clear();sessions.Read(current=>current.Acceptance(day),default);sessions.Read(current=>current.Acceptance(day),default);
        Check(reads.Count==4&&reads.Values.All(x=>x==2),"两个独立快照各自重新核验全部原件");
        reads.Clear();repository.Points(key);repository.Points(key);
        Check(reads[own]==2,"非只读事务的写入/后台仓储不复用旧结论");
        reads.Clear();
        using(var locked=new FileStream(Path.Combine(rawRoot,orphan+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            try{sessions.Read(current=>{current.Points(key);return current.Acceptance(day);},default);throw new Exception("全池跳过了未核验原件");}
            catch(IOException){Check(true,"缩小查询转为全池时，范围外锁定原件仍使查询失败");}
        }
        reads.Clear();failHash=own;
        sessions.Read(current=>
        {
            try{current.Acceptance(key,day);throw new Exception("合成读取故障未触发");}
            catch(IOException){Check(true,"批次I/O故障不转换为可用验收");}
            failHash=null;
            Check(current.Acceptance(key,day).Single().AcceptedDays==1,"同快照读取故障后必须重新完成整批核验");
            return true;
        },default);
        Check(reads[own]==2&&reads[calendar]==2,"失败批次没有发布部分核验成功供后续分量复用");
        File.WriteAllText(Path.Combine(rawRoot,own+".raw"),"tampered after previous completed query");
        try{sessions.Read(current=>current.Points(key),default);throw new Exception("新快照复用了损坏前的成功");}
        catch(InvalidDataException){Check(true,"上一查询结束后的损坏在新快照立即失败关闭");}
        File.WriteAllBytes(Path.Combine(rawRoot,own+".raw"),ownBytes);
        Check(sessions.Read(current=>current.Points(key),default).Single().Shares==100,"恢复原字节后新快照重新核验，不沿用损坏结论");
        reads.Clear();using var cancelled=new CancellationTokenSource();
        try{sessions.Read(current=>{current.Points(key);cancelled.Cancel();return current.Acceptance(key,day);},cancelled.Token);throw new Exception("复用分支忽略取消");}
        catch(OperationCanceledException){Check(true,"已核验原件的复用分支仍响应当前查询取消");}
        Check(sessions.Read(current=>current.Points(key),default).Single().Shares==100,"取消作用域释放后新快照仍可重新核验");
        reads.Clear();sessions.Read(current=>{current.Points(key);sessions.Read(inner=>inner.Points(key),default);return current.Points(key);},default);
        Check(reads[own]==2,"嵌套独立快照不共享核验结果，外层仍只核验一次");
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Scope="IsolatedSynthetic",Passed=passed,Installed=false,Boundary="Only evidence reuse within one independent read transaction; no notification or cross-query cache."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS 快照核验合计{passed}；正式查询性能须另验。");
    }
}
