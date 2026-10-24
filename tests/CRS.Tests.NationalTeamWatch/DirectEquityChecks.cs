using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class DirectEquityChecks
{
    internal const string CalendarHtml="2026年部分节假日休市安排，周末休市。1月1日（星期四）至1月3日（星期六）休市；2月15日（星期日）至2月23日（星期一）休市；4月4日（星期六）至4月6日（星期一）休市；5月1日（星期五）至5月5日（星期二）休市；6月19日（星期五）至6月21日（星期日）休市；9月25日（星期五）至9月27日（星期日）休市；10月1日（星期四）至10月7日（星期三）休市";
    internal static byte[] Source(DateOnly date,params (string Account,decimal Shares,decimal Percent)[] holders)=>JsonSerializer.SerializeToUtf8Bytes(new{sqlId="COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",actionErrors=Array.Empty<string>(),pageHelp=new{total=holders.Length},result=holders.Select(h=>new{REPORT_DATE=date.ToString("yyyy-MM-dd"),STOCK_ID="600036",NAME=h.Account,NUMBER_END=h.Shares,RATIO=h.Percent})});
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 直接持股/可靠性："+label);}
    private static void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception(label);}
    internal static void Seed(MarketRepository repository)
    {
        var uri=OfficialEquitySource.Request("SSE","600036");
        repository.SaveEquityDisclosure("SSE","600036",uri,Source(new(2026,4,28),("中央汇金资产管理有限责任公司",1000,10),("全国社保基金一零一组合",500,5),("基本养老保险基金一零零一组合",200,2)));
        var first=repository.EquityDisclosures(new("SSE","600036")).Single();
        repository.ReviewEquity(new(first.Id,new(2026,3,31),new(2026,4,28),"https://www.sse.com.cn/fixture-first.pdf","P10 股东表","test-reviewer",true,"Portfolio",Account:"中央汇金资产管理有限责任公司",OriginalConfirmed:true),"%PDF-synthetic-first"u8.ToArray());
        foreach(var account in new[]{"全国社保基金一零一组合","基本养老保险基金一零零一组合"})
            repository.ReviewEquity(new(first.Id,new(2026,3,31),new(2026,4,28),"https://www.sse.com.cn/fixture-first.pdf","P10 股东表","test-reviewer",true,"Portfolio",Account:account,OriginalConfirmed:true),"%PDF-synthetic-first"u8.ToArray());
        repository.SaveEquityDisclosure("SSE","600036",uri,Source(new(2026,8,28),("中央汇金资产管理有限责任公司",900,9),("全国社保基金一零一组合",550,5.5m)));
        var second=repository.EquityDisclosures(new("SSE","600036")).First(x=>x.State=="NeedsReview");
        repository.ReviewEquity(new(second.Id,new(2026,6,30),new(2026,8,28),"https://www.sse.com.cn/fixture-second.pdf","P11 股东表","test-reviewer",true,"Portfolio",Account:"中央汇金资产管理有限责任公司",OriginalConfirmed:true){ComparisonDisclosureId=first.Id},"%PDF-synthetic-second"u8.ToArray());
        repository.ReviewEquity(new(second.Id,new(2026,6,30),new(2026,8,28),"https://www.sse.com.cn/fixture-second.pdf","P11 股东表","test-reviewer",true,"Portfolio",Account:"全国社保基金一零一组合",OriginalConfirmed:true){ComparisonDisclosureId=first.Id},"%PDF-synthetic-second"u8.ToArray());
    }
    public static void Run(string root)
    {
        Check(InstitutionAccounts.Group("中央汇金投资有限责任公司")=="汇金投资"&&InstitutionAccounts.Group("中央汇金资产管理有限责任公司")=="汇金资管","汇金法律主体分别识别");
        Check(InstitutionAccounts.Group("全国社保基金一零一组合")=="社保基金"&&InstitutionAccounts.Group("基本养老保险基金一零零一组合")=="养老保险基金","社保及养老具体组合独立识别");
        Check(InstitutionAccounts.Group("汇金概念投资基金") is null&&InstitutionAccounts.Group("香港中央结算有限公司") is null,"不通过模糊名称归因持有人");
        Check(InstitutionAccounts.Group("汇金公司") is null&&InstitutionAccounts.CandidateGroup("汇金公司")=="主体待核验"&&InstitutionAccounts.CandidateGroup("社保基金会")=="主体待核验","官网简称仅生成主体待核验候选");
        Check(DirectEquityComparison.Compare(null,100,true,false)==("FirstListed",null)&&DirectEquityComparison.Compare(100,null,true,true)==("NotListed",null),"首次列示和未列示均不当作零持仓");
        Check(DirectEquityComparison.Compare(100,200,false,true)==("NotComparable",null),"股类与公司行为未经核验不计算增减持");
        var raw=new RawEvidenceStore(Path.Combine(root,"equity-raw"));using var repository=TestMarketData.Open(Path.Combine(root,"equity-data"),raw);
        var bytes=Source(new(2026,8,28),("中央汇金资产管理有限责任公司",900,9));var uri=OfficialEquitySource.Request("SSE","600036");
        var parsed=OfficialEquitySource.Parse("600036",bytes);Check(parsed.SourceDate==new DateOnly(2026,8,28),"源日期保存但不冒充报告截止日");
        Reject(()=>OfficialEquitySource.Parse("601398",bytes),"错误证券响应拒绝");
        repository.SaveEquityDisclosure("SSE","600036",uri,bytes);repository.SaveEquityDisclosure("SSE","600036",uri,bytes);
        Check(repository.EquityDisclosures(new("SSE","600036")).Count==1,"同原件重抓不重复候选");
        var row=repository.EquityHoldings(null,null,null,false).Single();Check(row.ReportAsOf is null&&row.Published is null&&row.Delta is null&&row.State=="NeedsReview","未经PDF核对不产生持仓期或交易结论");
        Reject(()=>repository.ReviewEquity(new(row.DisclosureId,new(2026,6,30),new(2026,8,28),"https://www.sse.com.cn/report.pdf","P1","reviewer",true,"Portfolio",Account:row.Account),"%PDF-test"u8.ToArray()),"缺少原文确认拒绝");
        var seedRaw=new RawEvidenceStore(Path.Combine(root,"equity-seed-raw"));using var seed=TestMarketData.Open(Path.Combine(root,"equity-seed-data"),seedRaw);Seed(seed);
        var values=seed.EquityHoldings(null,null,null,false);
        Check(values.Single(x=>x.Institution=="汇金资管") is {Delta:-100,State:"Decreased"},"同主体同口径披露间净减持");
        Check(values.Single(x=>x.Institution=="社保基金") is {Delta:50,State:"Increased"},"具体社保组合增持单独比较");
        Check(values.Single(x=>x.Institution=="养老保险基金") is {Shares:null,Delta:null,State:"NotListed"},"养老组合退出榜单仍为未知而非清仓");
        Check(seed.EquityHoldings("一零一","社保基金",null,false).Count==1&&seed.EquityHoldings("不存在",null,null,false).Count==0,"服务端机构和关键词筛选在分页前完成");
        seed.SaveEquityWatch(new("SSE","600036","招商银行",true,true));Check(seed.EquityHoldings(null,null,null,true).Count==3,"关注列表由后台持久化");
        var expected=seedRaw.Save("%PDF-synthetic-second"u8.ToArray());File.WriteAllText(Path.Combine(root,"equity-seed-raw",expected+".raw"),"broken");
        seed.ReconcileEvidenceIntegrity();
        Check(seed.EquityHoldings(null,null,null,false).Where(x=>x.ReportAsOf==new DateOnly(2026,6,30)).All(x=>x.Shares is null&&x.Delta is null&&x.State=="Critical"),"正式报告原件损坏阻断持仓及变动");
        File.WriteAllBytes(Path.Combine(root,"equity-seed-raw",expected+".raw"),"%PDF-synthetic-second"u8.ToArray());
        seed.ReconcileEvidenceIntegrity();
        Check(seed.EquityHoldings(null,null,null,false).Single(x=>x.Institution=="汇金资管").Delta==-100,"原件恢复到原哈希后解除阻断");
        var events=seed.EquityEvents(new("SSE","600036"));
        Check(events.Count==6&&events.Count(x=>x.State=="FirstListed")==3&&events.Single(x=>x.Account=="全国社保基金一零一组合"&&x.ReportAsOf==new DateOnly(2026,6,30)).Delta==50,"历次变动保留每个报告期与首次列示");
        seed.SaveEquityDisclosure("SSE","600036",uri,Source(new(2026,10,16),("中央汇金资产管理有限责任公司",800,8),("全国社保基金一零一组合",600,6)));
        var third=seed.EquityDisclosures(new("SSE","600036")).Single(x=>x.State=="NeedsReview");
        seed.ReviewEquity(new(third.Id,new(2026,9,30),new(2026,10,16),"https://www.sse.com.cn/fixture-third.pdf","P12","reviewer",false,"Portfolio",Account:"中央汇金资产管理有限责任公司",OriginalConfirmed:true),"%PDF-third"u8.ToArray());
        Check(seed.EquityHoldings(null,null,null,false).Single(x=>x.Institution=="社保基金") is {State:"NeedsReview",Delta:null},"同报告其他账户未经核验不得继承确认");
        seed.ReviewEquity(new(third.Id,new(2026,9,30),new(2026,10,16),"https://www.sse.com.cn/fixture-third.pdf","P12","reviewer",true,"Portfolio",Account:"全国社保基金一零一组合",OriginalConfirmed:true){ComparisonDisclosureId=seed.EquityDisclosures(new("SSE","600036")).Single(x=>x.ReportAsOf==new DateOnly(2026,6,30)).Id},"%PDF-third"u8.ToArray());
        Check(seed.EquityHoldings(null,null,null,false).Single(x=>x.Institution=="汇金资管") is {State:"NotComparable",Delta:null},"后核验账户不覆盖其他账户的可比性");
        Check(seed.EquityEvents(new("SSE","600036")).Count(x=>x.ReportAsOf==new DateOnly(2026,6,30))==3,"新增报告不抹去历史变动");
        Check(seed.EquityEvents(new("SSE","600036")).First().ComparisonFrom==new DateOnly(2026,6,30),"变动明确标记比较起始报告期而非每日交易");
        Reject(()=>seed.ReviewEquity(new(third.Id,new(2026,9,30),new(2026,10,16),"https://www.sse.com.cn/replaced.pdf","P12","reviewer",true,"Portfolio",Account:"全国社保基金一零一组合",OriginalConfirmed:true),"%PDF-replaced"u8.ToArray()),"替换同披露原文不得继承既有账户核验");
        var aliasRaw=new RawEvidenceStore(Path.Combine(root,"equity-alias-raw"));using var aliases=TestMarketData.Open(Path.Combine(root,"equity-alias-data"),aliasRaw);
        foreach(var (sourceDate,reportDate,institution,shares) in new[]{(new DateOnly(2026,4,28),new DateOnly(2026,3,31),"汇金投资",100m),(new DateOnly(2026,8,28),new DateOnly(2026,6,30),"汇金资管",200m)})
        {
            aliases.SaveEquityDisclosure("SSE","600036",uri,Source(sourceDate,("汇金公司",shares,1)));
            var candidate=aliases.EquityDisclosures(new("SSE","600036")).Single(x=>x.State=="NeedsReview");
            aliases.ReviewEquity(new(candidate.Id,reportDate,sourceDate,"https://www.sse.com.cn/alias.pdf","P1","reviewer",true,"Portfolio",Account:"汇金公司",OriginalConfirmed:true,Institution:institution){ComparisonDisclosureId=aliases.EquityDisclosures(new("SSE","600036")).FirstOrDefault(x=>x.State=="Accepted"&&x.ReportAsOf<reportDate)?.Id},"%PDF-alias"u8.ToArray());
        }
        Check(aliases.EquityHoldings(null,null,null,false).Single() is {State:"NotComparable",Delta:null},"相同简称核验为不同法人主体时禁止计算增减持");
        Reliability(root);
    }
    private static void Reliability(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"reliability-raw"));using var repository=TestMarketData.Open(Path.Combine(root,"reliability-data"),raw);
        string Fetch(string text)=>repository.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(text)),"https://www.sse.com.cn/",provider:"OfficialSseEtfJson");
        var key=new EtfKey("SSE","510300");var first=new DateOnly(2026,9,1);var second=first.AddDays(1);
        repository.ImportSharesDetailed(Fetch("later"),[new(key,second,1000,"份")],default,false);
        repository.ImportSharesDetailed(Fetch("earlier"),[new(key,first,100,"份")],default,false);
        Check(repository.Evidence().Count(x=>x.State=="NeedsReview")==1&&repository.Points(key).Count==1,"倒序补历史检查与后继日的异常跳变");
        var hash=raw.Save("later"u8.ToArray());File.WriteAllText(Path.Combine(root,"reliability-raw",hash+".raw"),"corrupted");
        repository.ReconcileEvidenceIntegrity();Check(repository.Points(key).Count==0,"受控巡检隔离损坏证据，拒绝参与计算");
        File.WriteAllBytes(Path.Combine(root,"reliability-raw",hash+".raw"),"later"u8.ToArray());
        repository.ReconcileEvidenceIntegrity();Check(repository.Points(key).Count==1,"原字节恢复经巡检解除阻断，无需清库");
        foreach(var market in new[]{"SSE","SZSE"})repository.RegisterReviewedCalendar(market,2026,Encoding.UTF8.GetBytes(CalendarHtml),ReviewedExchangeCalendar.Source(market,2026));
        Check(repository.IsReviewedTradingDay("SSE",new(2026,10,10))==false&&repository.IsReviewedTradingDay("SZSE",new(2026,10,1))==false&&repository.IsReviewedTradingDay("SSE",new(2026,10,8))==true,"官方年度公告区分假期、周末和调休工作日");
        Reject(()=>ReviewedExchangeCalendar.Parse("SSE",2026,CalendarHtml.Replace("10月7日","10月8日")),"休市公告范围变化停止登记");
        var friday=new DateOnly(2026,10,9);var saturday=friday.AddDays(1);var monday=friday.AddDays(3);
        var records=Enumerable.Range(0,5).Select(i=>new MarketActivityPoint(friday.AddDays(i-4),100,10,"Complete")).Append(new(saturday,null,null,"Closed")).Append(new(monday,120,10,"Complete")).ToArray();
        Check(MarketActivityMetrics.Calculate(records).Last().Mean5==100,"已核验休市日不会清空成交比较窗口");
        foreach(var market in new[]{"SSE","SZSE"})repository.SaveMarketActivity(market,saturday,null);
        Check(repository.MarketActivity().Single().State=="Closed","旧周末来源故障按已核验日历显示休市");
        MarketActivitySource Source(string market,string tag,decimal amount)=>new(new("https://www.sse.com.cn/"),Encoding.UTF8.GetBytes(tag),market,friday,new(market,friday,amount,1));
        repository.SaveMarketActivity("SSE",friday,Source("SSE","a",100));repository.SaveMarketActivity("SZSE",friday,Source("SZSE","b",100));repository.SaveMarketActivity("SSE",friday,Source("SSE","c",101));
        Check(repository.MarketActivity().Single(x=>x.Date==friday).State=="Conflict","不同成交值先阻断");
        repository.ReviewMarketActivity(repository.MarketActivityRevisions().Single(x=>x.AmountYuan==101).Id,true,"reviewer");
        Check(repository.MarketActivity().Single(x=>x.Date==friday).AmountYuan==201,"审核成交修订恢复汇总并保留旧原件");
        repository.SaveMarketActivity("SSE",friday,Source("SSE","d",102));Check(repository.MarketActivity().Single(x=>x.Date==friday).State=="Conflict","后续新修订重新触发冲突");
    }
}
