using System.Text;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>历史补入、更正及阶段重放的反例；只操作隔离库，不代表真实披露或连续采集验收。</summary>
internal static class EvidenceAcceptanceAdversarialChecks
{
    private const string Account="中央汇金资产管理有限责任公司";
    private static void Check(bool ok,string label)
    {if(!ok)throw new Exception(label);Console.WriteLine("PASS 证据反方回归："+label);}

    public static void Run(string root)
    {
        EquityBaseline(Path.Combine(root,"equity"));
        CollectionReceipt(Path.Combine(root,"collection"));
    }

    private static void EquityBaseline(string root)
    {
        var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var key=new EtfKey("SSE","600036");string latestId;
        using(var repository=TestMarketData.Open(data,raw))
        {
            DirectEquityChecks.Seed(repository);
            latestId=repository.EquityDisclosures(key).Single(x=>x.ReportAsOf==new DateOnly(2026,6,30)).Id;
            EquityHoldingRow Current()=>repository.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account);
            EquityReview LatestReview(string? prior)=>new(latestId,new(2026,6,30),new(2026,8,28),
                "https://www.sse.com.cn/fixture-second.pdf","P11 股东表","baseline-reviewer",true,"Portfolio",Account:Account,OriginalConfirmed:true){ComparisonDisclosureId=prior};
            void ReviewLatest()=>repository.ReviewEquity(LatestReview(repository.EquityDisclosures(key)
                .Where(x=>x.State=="Accepted"&&x.ReportAsOf<new DateOnly(2026,6,30)).OrderByDescending(x=>x.ReportAsOf).First().Id),"%PDF-synthetic-second"u8.ToArray());
            string AddPrior(DateOnly source,decimal shares,string tag)
            {
                repository.SaveEquityDisclosure(key.Market,key.Ticker,OfficialEquitySource.Request(key.Market,key.Ticker),DirectEquityChecks.Source(source,(Account,shares,9.5m)));
                var candidate=repository.EquityDisclosures(key).Single(x=>x.State=="NeedsReview");
                repository.ReviewEquity(new(candidate.Id,new(2026,5,31),source,"https://www.sse.com.cn/"+tag+".pdf",
                    "P10 股东表","baseline-reviewer",true,"Portfolio",Account:Account,OriginalConfirmed:true){ComparisonDisclosureId=repository.EquityDisclosures(key).Single(x=>x.ReportAsOf==new DateOnly(2026,3,31)).Id},Encoding.UTF8.GetBytes("%PDF-"+tag));
                return candidate.Id;
            }
            Check(Current() is {Delta:-100,State:"Decreased"},"相邻披露在审核时已存在才计算原差额");
            var staleReview=LatestReview(repository.EquityDisclosures(key).Single(x=>x.ReportAsOf==new DateOnly(2026,3,31)).Id);
            var inserted=AddPrior(new(2026,6,15),950,"inserted-history");
            Check(Current() is {Shares:900,Delta:null,State:"NotComparable",EvidenceLevel:EvidenceLevel.DisclosedHolding},"补入前期披露后旧可比审核失效，保留A类当前快照");
            Check(repository.EquityEvents(key).Single(x=>x.DisclosureId==latestId&&x.Account==Account).Delta is null,"历次变动查询同样拒绝沿用过期基线");
            try{repository.ReviewEquity(staleReview,"%PDF-synthetic-second"u8.ToArray());throw new Exception("旧界面审核被接受。");}
            catch(InvalidDataException){Check(Current().Delta is null,"界面加载之后前期变化，拒绝旧确认且不修改快照");}
            try{repository.ReviewEquity(LatestReview(null),"%PDF-synthetic-second"u8.ToArray());throw new Exception("缺失基线被接受。");}
            catch(InvalidDataException){Check(true,"已有前期时旧客户端未指定基线也拒绝可比审批");}
            ReviewLatest();
            Check(Current() is {Delta:-50,ComparisonFrom:var from}&&from==new DateOnly(2026,5,31),"重新逐账户审核后使用实际新前期，差额仍是报告期变化");
            var replacement=AddPrior(new(2026,6,16),960,"corrected-history");
            Check(repository.EquityDisclosures(key).Single(x=>x.Id==inserted).State=="Superseded"&&Current().Delta is null,"同报告期更正换了披露ID也必须重新审核后期基线");
            ReviewLatest();
            Check(Current() is {Delta:-60,State:"Decreased"}&&inserted!=replacement,"更正后重审仅采用更正版，不重复计算旧版");
        }
        using(var reopened=TestMarketData.Open(data,raw))
            Check(reopened.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account).Delta==-60,"基线和重新审核结果跨重启保留");
        // 模拟旧版已审核库：不凭既有comparable=true补造缺失的基线记录。
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Password=Convert.ToHexString(TestMarketData.Key),Pooling=false}.ToString()))
        {
            db.Open();using var audit=db.CreateCommand();
            audit.CommandText="SELECT count(DISTINCT b.prior_disclosure_id) FROM equity_account_comparison_baseline b JOIN equity_account_review_audit a ON a.id=b.review_id WHERE a.disclosure_id=$id";audit.Parameters.AddWithValue("$id",latestId);
            Check(Convert.ToInt32(audit.ExecuteScalar())==3,"各次可比审核的不同基线追加留证，不被重审覆盖");
            using var command=db.CreateCommand();command.CommandText="DROP TABLE equity_account_comparison_baseline";command.ExecuteNonQuery();
        }
        using(var legacy=TestMarketData.Open(data,raw))
            Check(legacy.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account) is {Shares:900,Delta:null,State:"NotComparable",EvidenceLevel:EvidenceLevel.DisclosedHolding},"旧库只创建空基线表，不追溯批准旧比较或丢失快照");
    }

    private static void CollectionReceipt(string root)
    {
        var day=new DateOnly(2026,9,2);var key=new EtfKey("SSE","510300");
        int Count(string name,DateTimeOffset received,DateTimeOffset logged)
        {
            var clock=new ManualClock{Value=received};var raw=new RawEvidenceStore(Path.Combine(root,name,"raw"));
            using var repository=TestMarketData.Open(Path.Combine(root,name,"data"),raw,timeProvider:clock);
            foreach(var fund in repository.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))
                repository.SetMembership(new(fund.Market,fund.Ticker),fund.Name,fund.IndexGroup,false,"receipt-fixture");
            repository.RegisterReviewedCalendar("SSE",2026,Encoding.UTF8.GetBytes(DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source("SSE",2026));
            var fetch=repository.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(name)),"https://www.sse.com.cn/",provider:"OfficialSseEtfJson");
            repository.ImportSharesDetailed(fetch,[new(key,day,100,"万份")],default,false);
            clock.Value=logged;repository.RecordCollection(repository.BeginCollection(key,day),"Accepted","AcceptedFactVerified",fetch);
            clock.Value=new(2026,9,4,0,0,0,TimeSpan.Zero);
            return repository.Acceptance(key,day).Single().ContinuousCollectionDays;
        }
        Check(Count("old-receipt",new(2026,9,1,0,0,0,TimeSpan.Zero),new(2026,9,2,12,0,0,TimeSpan.Zero))==0,"旧原件今日重放Accepted阶段不能制造真实日采集");
        Check(Count("same-day",new(2026,9,2,12,0,0,TimeSpan.Zero),new(2026,9,2,12,1,0,TimeSpan.Zero))==1,"当天实际接收原件并接受才累计当日");
        Check(Count("next-day",new(2026,9,3,15,59,0,TimeSpan.Zero),new(2026,9,3,15,59,1,TimeSpan.Zero))==1,"北京时间次日迟发仍在既有日采集窗口内");
        Check(Count("late-receipt",new(2026,9,3,16,0,0,TimeSpan.Zero),new(2026,9,3,16,0,1,TimeSpan.Zero))==0,"北京时间越过次日边界的回填不累计连续日");
        Check(Count("late-acceptance",new(2026,9,2,12,0,0,TimeSpan.Zero),new(2026,9,3,16,0,0,TimeSpan.Zero))==0,"及时原件但超过窗口才接受的旧记录仍不冒充及时采集");
    }
    private sealed class ManualClock:TimeProvider
    {public DateTimeOffset Value;public override DateTimeOffset GetUtcNow()=>Value;}
}
