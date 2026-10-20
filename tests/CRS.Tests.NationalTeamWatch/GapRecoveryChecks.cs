using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>日历与补采隔离回归，不将合成250日或回填20日视作真实来源验收。</summary>
internal static class GapRecoveryChecks
{
    internal const string Calendar2025="2025年部分节假日休市安排；周末休市；1月1日（星期三）休市；1月28日（星期二）至2月4日（星期二）休市；4月4日（星期五）至4月6日（星期日）休市；5月1日（星期四）至5月5日（星期一）休市；5月31日（星期六）至6月2日（星期一）休市；10月1日（星期三）至10月8日（星期三）休市";
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 缺口补采："+label);}
    public static async Task Run(string root)
    {
        foreach(var market in new[]{"SSE","SZSE"})
        {
            var days=ReviewedExchangeCalendar.Parse(market,2025,Calendar2025);
            Check(days.Count(x=>x.Open)==243&&!days.Single(x=>x.Date==new DateOnly(2025,10,8)).Open&&days.Single(x=>x.Date==new DateOnly(2025,10,9)).Open,"2025官方安排含单日元旦、跨月春节和合并节日，调休周末不开市");
        }
        var data=Path.Combine(root,"gap-data");var raw=new RawEvidenceStore(Path.Combine(root,"gap-raw"));var key=new EtfKey("SSE","510300");
        using(var repository=TestMarketData.Open(data,raw))
        {
            foreach(var item in repository.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(item.Market,item.Ticker),item.Name,item.IndexGroup,false,"isolated-gap-fixture");
            repository.RegisterReviewedCalendar("SSE",2025,Encoding.UTF8.GetBytes(Calendar2025),ReviewedExchangeCalendar.Source("SSE",2025));
            repository.RegisterReviewedCalendar("SSE",2026,Encoding.UTF8.GetBytes(DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source("SSE",2026));
            var from=new DateOnly(2025,12,29);var to=new DateOnly(2026,1,6);var now=DateTimeOffset.UtcNow;
            repository.RecordSyncCursor(new(2026,10,18));
            Check(repository.DueShareGaps(from,to,now).Count==5,"游标已前进仍识别跨年历史缺口，不把元旦和周末算缺失");
            var id=repository.BeginCollection(key,from);repository.RecordCollection(id,"AccessBlocked","Http429");
            Check(repository.DueShareGaps(from,to,now.AddSeconds(2)).First().Date==from.AddDays(1),"访问受限日期冷却，未尝试日期继续前进");
            Check(repository.DueShareGaps(from,to,now.AddHours(7)).Any(x=>x.Date==from),"冷却到期旧缺口重新入队");
            repository.RecordCollection(repository.BeginCollection(key,from),"Interrupted","FixtureRestart");
            Check(repository.DueShareGaps(from,to,now.AddSeconds(2)).Any(x=>x.Date==from),"重启后中断缺口可重试，不依赖内存队列");
            var missing=new DateOnly(2025,1,2);var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
            var days=ReviewedExchangeCalendar.Parse("SSE",2025,Calendar2025).Concat(ReviewedExchangeCalendar.Parse("SSE",2026,DirectEquityChecks.CalendarHtml)).Where(x=>x.Open&&x.Date<today.AddDays(-7)&&x.Date!=missing).Select(x=>new ImportRow(key,x.Date,100,"万份")).ToArray();
            var fetch=repository.RecordFetch(raw.Save("synthetic reviewed historical shares"u8.ToArray()),"https://www.sse.com.cn/",provider:"OfficialSseEtfJson");repository.ImportSharesDetailed(fetch,days,default,false);
            Check(repository.DueHistoricalShareGaps(today.AddDays(-8),now.AddSeconds(2)).Single().Date==missing,"全部已登记历史分段扫描，可找回400天以前的旧缺口");
            var acceptance=repository.Acceptance(today.AddDays(-1)).Single();
            Check(!acceptance.Continuous20Passed&&acceptance.ContinuousCollectionDays==0,"大量历史回填不能冒充20个真实交易日连续采集");
            var comparisonKey=new EtfKey("SSE","510050");repository.SetMembership(comparisonKey,"isolated","index",true,"isolated gap comparison");
            string Fetch(string tag)=>repository.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(tag)),"https://www.sse.com.cn/",provider:"OfficialSseEtfJson");
            repository.ImportSharesDetailed(Fetch("synthetic later year"),[new(comparisonKey,new(2026,9,1),1000,"份")],default,false);
            repository.ImportSharesDetailed(Fetch("synthetic earlier year"),[new(comparisonKey,new(2025,9,1),100,"份")],default,false);
            Check(repository.Points(comparisonKey).Count==2&&!repository.Evidence().Any(x=>x.Ticker==comparisonKey.Ticker&&x.State=="NeedsReview"),"长期回填差额不当作相邻交易日跳变，跨缺口日指标仍不可算");
            // 批读取减少重复扫描，但必须继续隔离真实损坏，并在恢复同一哈希后重新核验。
            var beforeBatch=repository.PointsForKeys([key,comparisonKey]);
            var original="synthetic earlier year"u8.ToArray();var originalHash=raw.Save(original);var originalPath=Path.Combine(root,"gap-raw",originalHash+".raw");
            File.WriteAllBytes(originalPath,"damaged fixture"u8.ToArray());
            try{repository.PointsForKeys([key,comparisonKey]);throw new Exception("未登记损坏未拒绝查询。");}
            catch(InvalidDataException){Check(true,"后台协调前批查询失败关闭，不偷偷修复状态");}
            repository.ReconcileEvidenceIntegrity();
            var damagedBatch=repository.PointsForKeys([key,comparisonKey]);
            Check(damagedBatch[key].Count==beforeBatch[key].Count&&damagedBatch[comparisonKey].Count==1,"批事实查询仍隔离损坏原件，不抹去其它证券的有效事实");
            File.WriteAllBytes(originalPath,original);
            repository.ReconcileEvidenceIntegrity();
            Check(repository.PointsForKeys([key,comparisonKey])[comparisonKey].Count==2,"批查询下次重新核验原件，同哈希恢复后解除阻断");
            repository.SetMembership(comparisonKey,"isolated","index",false,"end isolated gap comparison");
        }
        var source=new RecordingSource();
        await using(var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(data,raw),raw))
        {
            await workspace.InitializeAsync(default);workspace.SetOfficialSource(source);await workspace.SyncRecentAsync(default);
            var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
            Check(source.Calls.First()>=today.AddDays(-7)&&source.Calls.Contains(new(2025,1,2)),"最新数据先采，再补旧缺口，旧游标不阻塞最新日期");
            Check((await workspace.PointsAsync(key,default)).Any(x=>x.Date==new DateOnly(2025,1,2)),"缺口经实际应用用例提交为有效事实");
        }
    }
    private sealed class RecordingSource:IOfficialShareSource
    {
        public List<DateOnly> Calls {get;}=[];
        public Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)
        {
            Calls.Add(day);return Task.FromResult(new SourceDay(day,[new(new("https://www.sse.com.cn/"),Encoding.UTF8.GetBytes("synthetic "+day),[new(new("SSE","510300"),day,100,"万份")])]));
        }
    }
}
