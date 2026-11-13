using System.Collections.Concurrent;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>真实加密隔离库与原件读取计数验证批量验收；不使用磁盘计时阈值冒充正式性能。</summary>
internal static class AcceptanceBatchChecks
{
    public static void Run(string root)
    {
        var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 验收批量："+label);}
        var rawPath=Path.Combine(root,"raw");var reads=new ConcurrentDictionary<string,int>(StringComparer.Ordinal);
        using var cancelled=new CancellationTokenSource();string? cancelHash=null;
        var raw=new RawEvidenceStore(rawPath,openRead:path=>
        {reads.AddOrUpdate(Path.GetFileNameWithoutExtension(path),1,(_,n)=>n+1);if(Path.GetFileNameWithoutExtension(path)==cancelHash)cancelled.Cancel();return File.OpenRead(path);});
        var clock=new ManualClock();using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);
        var keys=Enumerable.Range(0,130).Select(i=>new EtfKey(i%2==0?"SSE":"SZSE",(600000+i).ToString())).ToArray();
        var cross=new EtfKey("SZSE",keys[0].Ticker);var inactive=new EtfKey("SSE","699999");var funds=keys.Append(cross).Append(inactive).ToArray();
        foreach(var key in funds)repo.SetMembership(key,"合成基金","batch",true,"隔离批量夹具");
        var days=Enumerable.Range(0,3).Select(i=>new DateOnly(2026,9,1).AddDays(i)).ToArray();
        var hash=raw.Save("synthetic shared original: receipt times are distinct source_fetch records"u8.ToArray());
        var attempts=new Dictionary<(EtfKey,DateOnly),string>();
        foreach(var day in days)
        {
            clock.Value=new(day.Year,day.Month,day.Day,12,0,0,TimeSpan.FromHours(8));
            var fetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-acceptance");
            repo.ImportCalendar(fetch,[("SSE",day),("SZSE",day)],default);
            repo.ImportShares(fetch,funds.Select(key=>new CRS.Plugins.NationalTeamWatch.Application.ImportRow(key,day,1000,"份")).ToArray(),default);
            foreach(var key in funds)
            {
                var id=repo.BeginCollection(key,day);repo.RecordCollection(id,"Accepted","AcceptedFactVerified",fetch);attempts[(key,day)]=id;
            }
        }
        clock.Value=new(2026,9,4,12,0,0,TimeSpan.FromHours(8));
        repo.SetMembership(inactive,"停用合成基金","batch",false,"已有事实后停用");
        var through=days[^1];
        reads.Clear();var profile=repo.ExecuteOperation(()=>repo.ProfileAcceptance(null,through),default);
        Check(reads.Count==1&&reads[hash]==1,"超过128个目标、多市场、多日共享原件在一次验收内只完整读取一次");
        File.WriteAllText(Path.Combine(root,"acceptance-profile.json"),JsonSerializer.Serialize(new{Scope="IsolatedSynthetic",SharedOriginalReads=reads[hash],Profile=profile},new JsonSerializerOptions{WriteIndented=true}));
        var active=repo.Configuration().Count(x=>x.Active);
        Check(profile.Acceptance.Count==active&&!profile.Acceptance.Any(x=>x.Ticker==inactive.Ticker),"全部启用证券保留，停用证券不进入分母");
        Check(profile.Acceptance.Where(x=>funds.Contains(new(x.Market,x.Ticker))).All(x=>x.AcceptedDays==3&&x.ContinuousCollectionDays==3),
            "跨批次证券连续三日事实和真实采集接收窗口完整保留");
        Check(profile.Acceptance.Where(x=>!funds.Contains(new(x.Market,x.Ticker))).All(x=>x.AcceptedDays==0&&x.UnresolvedDays==3),
            "无资料内置证券仍显示三日缺口，不能缩小覆盖分母");
        Check(profile.Acceptance.All(x=>!x.CalendarVerified&&!x.DataAcceptancePassed),"未审核交易日历的合成连续记录仍不能通过正式验收");
        var stages=profile.Stages.Select(x=>x.Name).ToArray();
        Check(stages.Contains("EvidenceSql")&&stages.Contains("RawVerificationIncludingAdmission")&&stages.Contains("AcceptanceReceiptsSql")
            &&profile.Stages.All(x=>double.IsFinite(x.ElapsedMs)&&x.ElapsedMs>=0&&x.Items>=0),"阶段诊断保留SQL、完整原件核验及投影计数");
        Check(JsonSerializer.Serialize(profile.Acceptance)==JsonSerializer.Serialize(repo.Acceptance(through)),"诊断与正常验收结果逐字段一致");
        reads.Clear();_ = repo.Acceptance(through);Check(reads[hash]==1,"下一次验收重新读原件，不跨请求缓存健康结论");

        repo.RecordCollection(attempts[(keys[0],through)],"Missing","SyntheticLatestStage");
        repo.RecordCollection(repo.BeginCollection(keys[1],through),"Missing","SyntheticNewAttempt");
        var after=repo.Acceptance(through);
        Check(after.Single(x=>x.Market==keys[0].Market&&x.Ticker==keys[0].Ticker).ContinuousCollectionDays==0,
            "同次尝试最新阶段已失败时，旧Accepted不能继续累计");
        Check(after.Single(x=>x.Market==keys[1].Market&&x.Ticker==keys[1].Ticker).ContinuousCollectionDays==3,
            "另一独立尝试失败，不抹除此前同日真实成功记录");
        Check(after.Single(x=>x.Market==cross.Market&&x.Ticker==cross.Ticker).ContinuousCollectionDays==3,
            "同代码跨市场独立，不能被另一市场的失败状态污染");

        // 份额与采集阶段可引用不同原件；单只验收也必须核验阶段原件，不能因初始核验范围不含它而漏检。
        clock.Value=new(2026,9,3,13,0,0,TimeSpan.FromHours(8));
        var receiptHash=raw.Save("synthetic separate receipt original"u8.ToArray());
        var receiptFetch=repo.RecordFetch(receiptHash,"https://www.sse.com.cn/synthetic-receipt");
        repo.RecordCollection(repo.BeginCollection(keys[0],through),"Accepted","AcceptedFactVerified",receiptFetch);
        clock.Value=new(2026,9,4,12,0,0,TimeSpan.FromHours(8));
        reads.Clear();var single=repo.Acceptance(keys[0],through).Single();
        Check(single.ContinuousCollectionDays==3&&reads.ContainsKey(receiptHash),"单证券补验未包含在份额原件中的采集阶段证据");
        cancelHash=receiptHash;
        try{repo.ExecuteOperation(()=>repo.ProfileAcceptance(keys[0],through),cancelled.Token);throw new Exception("补充原件核验未取消。");}
        catch(OperationCanceledException){Check(true,"额外采集原件的读取也响应当前查询取消");}
        cancelHash=null;
        Check(repo.ExecuteOperation(()=>repo.ProfileAcceptance(keys[0],through),default).Acceptance.Single().ContinuousCollectionDays==3,
            "取消释放诊断作用域，后续查询完整重验");
        File.WriteAllText(Path.Combine(rawPath,receiptHash+".raw"),"damaged");
        Check(repo.Acceptance(keys[0],through).Single().ContinuousCollectionDays==0,"独立采集原件损坏不能冒充及时采集");
        File.WriteAllBytes(Path.Combine(rawPath,receiptHash+".raw"),"synthetic separate receipt original"u8.ToArray());
        var orphan=raw.Save("synthetic orphan retained in full acceptance integrity scope"u8.ToArray());
        repo.RecordFetch(orphan,"https://www.sse.com.cn/synthetic-orphan");
        using(var locked=new FileStream(Path.Combine(rawPath,orphan+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            try{repo.Acceptance(through);throw new Exception("全池验收悄悄缩小原件范围。");}
            catch(IOException){Check(true,"全池验收保留原有完整性范围，未借优化跳过独立原件");}
        }
        clock.Value=new(2026,9,3,13,30,0,TimeSpan.FromHours(8));
        var unusedFetch=repo.RecordFetch(orphan,"https://www.sse.com.cn/synthetic-older-attempt");
        repo.RecordCollection(repo.BeginCollection(keys[1],through),"Accepted","AcceptedFactVerified",unusedFetch);
        var newestFetch=repo.RecordFetch(hash,"https://www.sse.com.cn/synthetic-newest-attempt");
        repo.RecordCollection(repo.BeginCollection(keys[1],through),"Accepted","AcceptedFactVerified",newestFetch);
        clock.Value=new(2026,9,4,12,0,0,TimeSpan.FromHours(8));
        using(var locked=new FileStream(Path.Combine(rawPath,orphan+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            Check(repo.Acceptance(keys[1],through).Single().ContinuousCollectionDays==3,
                "同日最新成功已经满足时，单证券验收不预读未使用的旧尝试原件");
        }
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"tampered shared original");
        try{repo.Acceptance(through);throw new Exception("跨查询复用了旧核验成功。");}
        catch(InvalidDataException){Check(true,"新损坏立即失败关闭，不返回空集合或沿用旧健康状态");}
        repo.ReconcileEvidenceIntegrity();
        Check(repo.Acceptance(through).All(x=>x.AcceptedDays==0),"已登记Q15的损坏份额继续排除，不按零份额接受");
        Console.WriteLine("READ 隔离验收诊断："+Path.Combine(root,"acceptance-profile.json"));
        Console.WriteLine($"PASS 验收批量合计{passed}；仅隔离合成原件，不证明已安装Worker超时已解决。");
    }
    private sealed class ManualClock:TimeProvider
    {public DateTimeOffset Value=new(2026,9,1,12,0,0,TimeSpan.FromHours(8));public override DateTimeOffset GetUtcNow()=>Value.ToUniversalTime();}
}
