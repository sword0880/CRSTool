using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private void InitializeEquitySchema()
    {
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS feature_schema_migration(feature TEXT PRIMARY KEY,version INTEGER NOT NULL,applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS equity_watch(market TEXT,ticker TEXT,name TEXT NOT NULL,active INTEGER NOT NULL,favorite INTEGER NOT NULL,PRIMARY KEY(market,ticker));
            CREATE TABLE IF NOT EXISTS equity_disclosure(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,source_date TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),document_fetch_id TEXT REFERENCES source_fetch(fetch_id),report_date TEXT,published TEXT,state TEXT NOT NULL,comparable INTEGER NOT NULL DEFAULT 0,nature TEXT NOT NULL DEFAULT 'Unknown',share_class TEXT NOT NULL DEFAULT 'Unknown',basis TEXT NOT NULL DEFAULT 'Unknown',page TEXT,reviewer TEXT,verified_at TEXT,UNIQUE(market,ticker,fetch_id));
            CREATE TABLE IF NOT EXISTS equity_holder(disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),account TEXT NOT NULL,shares TEXT NOT NULL,percent TEXT,PRIMARY KEY(disclosure_id,account));
            CREATE TABLE IF NOT EXISTS equity_account_review(disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),account TEXT NOT NULL,nature TEXT NOT NULL,PRIMARY KEY(disclosure_id,account));
            CREATE TABLE IF NOT EXISTS equity_account_identity(disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),account TEXT NOT NULL,institution TEXT NOT NULL,reviewer TEXT NOT NULL,PRIMARY KEY(disclosure_id,account));
            CREATE TABLE IF NOT EXISTS equity_account_comparability(disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),account TEXT NOT NULL,comparable INTEGER NOT NULL,PRIMARY KEY(disclosure_id,account));
            CREATE TABLE IF NOT EXISTS equity_account_review_audit(id TEXT PRIMARY KEY,disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),account TEXT NOT NULL,institution TEXT NOT NULL,nature TEXT NOT NULL,comparable INTEGER NOT NULL,reviewer TEXT NOT NULL,reviewed_at TEXT NOT NULL,document_hash TEXT NOT NULL,page TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS equity_review_audit(id TEXT PRIMARY KEY,disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),reviewer TEXT NOT NULL,reviewed_at TEXT NOT NULL,document_fetch_id TEXT NOT NULL,report_date TEXT NOT NULL,published TEXT NOT NULL,page TEXT NOT NULL);
            """);
        var version=Scalar("SELECT version FROM feature_schema_migration WHERE feature='direct-equity'");
        if(version is not null && Convert.ToInt32(version)!=1)throw new InvalidDataException("直接持股库版本不兼容。");
        Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('direct-equity',1,$now)",("$now",Now));
        // 这些是采集观察对象，不预置机构持有结论；停用与关注设置重启后保留。
        foreach(var (ticker,name) in new[]{("601398","工商银行"),("601288","农业银行"),("601988","中国银行"),("601939","建设银行"),("600036","招商银行"),("601318","中国平安"),("600519","贵州茅台"),("600887","伊利股份"),("600900","长江电力"),("600153","建发股份"),("603195","公牛集团"),("600426","华鲁恒升"),("600429","三元股份")})
            Execute("INSERT OR IGNORE INTO equity_watch VALUES('SSE',$t,$n,1,0)",("$t",ticker),("$n",name));
        transaction.Commit();
        InitializeEquityReportSchema();
    }
    public List<EquityWatch> EquityWatchlist()
    {
        var result=new List<EquityWatch>();using var command=Command("SELECT market,ticker,name,active,favorite FROM equity_watch ORDER BY favorite DESC,market,ticker");using var reader=command.ExecuteReader();
        while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3)==1,reader.GetInt32(4)==1));return result;
    }
    public void SaveEquityWatch(EquityWatch watch,Guid? commandId=null)
    {
        if(Committed(commandId))return;
        if(watch.Market is not ("SSE" or "SZSE") || watch.Ticker.Length!=6 || !watch.Ticker.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(watch.Name)||watch.Name.Length>100)throw new InvalidDataException("请填写市场、6位股票代码和名称。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("INSERT INTO equity_watch VALUES($m,$t,$n,$a,$f) ON CONFLICT(market,ticker) DO UPDATE SET name=excluded.name,active=excluded.active,favorite=excluded.favorite",("$m",watch.Market),("$t",watch.Ticker),("$n",watch.Name.Trim()),("$a",watch.Active?1:0),("$f",watch.Favorite?1:0));
        RecordCommand(commandId,"equity-watch");transaction.Commit();
    }
    public void SaveEquityDisclosure(string market,string ticker,Uri uri,byte[] bytes)
    {
        if(uri!=OfficialEquitySource.Request(market,ticker))throw new InvalidDataException("股东来源不匹配。");
        var parsed=OfficialEquitySource.Parse(ticker,bytes);
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(parsed.SourceDate>today)throw new InvalidDataException("股东来源日期在未来。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var hash=raw.Save(bytes);
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_disclosure d JOIN source_fetch f USING(fetch_id) WHERE d.market=$m AND d.ticker=$t AND f.sha256=$h",("$m",market),("$t",ticker),("$h",hash)))>0){transaction.Commit();return;}
        if(ReadEquityDisclosures(new(market,ticker)).Any(x=>x.SourceDate==parsed.SourceDate && x.State!="Critical" && x.Holders.OrderBy(h=>h.Account).SequenceEqual(parsed.Holders.OrderBy(h=>h.Account)))){transaction.Commit();return;}
        var fetch=RecordFetch(hash,uri.AbsoluteUri,hash,"OfficialSseTop10Xbrl","sse-top10-v1");var id=Guid.NewGuid().ToString("N");
        Execute("INSERT INTO equity_disclosure(id,market,ticker,source_date,fetch_id,state) VALUES($id,$m,$t,$d,$f,'NeedsReview')",("$id",id),("$m",market),("$t",ticker),("$d",parsed.SourceDate.ToString("yyyy-MM-dd")),("$f",fetch));
        foreach(var holder in parsed.Holders)Execute("INSERT INTO equity_holder VALUES($id,$a,$s,$p)",("$id",id),("$a",holder.Account),("$s",Decimal(holder.Shares)),("$p",holder.Percent is {} p?Decimal(p):null));
        Execute("UPDATE source_fetch SET status='Parsed' WHERE fetch_id=$f",("$f",fetch));transaction.Commit();
    }
    public List<EquityDisclosure> EquityDisclosures(EtfKey key){ValidateEvidence();return ReadEquityDisclosures(key);}
    private List<EquityDisclosure> ReadEquityDisclosures(EtfKey key)
    {
        var headers=new List<(string Id,DateOnly Date,DateOnly? Report,DateOnly? Published,string State,string Url,string Hash,string? Document,string? Page,string? Reviewer,bool Comparable,string Nature)>();
        using(var command=Command("""
            SELECT d.id,d.source_date,d.report_date,d.published,
            CASE WHEN d.state='Superseded' THEN 'Superseded' WHEN EXISTS(SELECT 1 FROM quality_issue q WHERE q.rule_id='Q15' AND q.status='Open' AND (q.evidence_id=d.fetch_id OR q.evidence_id=d.document_fetch_id)) THEN 'Critical' ELSE d.state END,
            f.source_url,f.sha256,p.source_url,d.page,d.reviewer,d.comparable,d.nature
            FROM equity_disclosure d JOIN source_fetch f ON f.fetch_id=d.fetch_id LEFT JOIN source_fetch p ON p.fetch_id=d.document_fetch_id WHERE d.market=$m AND d.ticker=$t ORDER BY coalesce(d.report_date,d.source_date) DESC,f.retrieved_utc DESC,d.id DESC
            """,("$m",key.Market),("$t",key.Ticker)))
        using(var reader=command.ExecuteReader())while(reader.Read())headers.Add((reader.GetString(0),DateOnly.Parse(reader.GetString(1),CultureInfo.InvariantCulture),reader.IsDBNull(2)?null:DateOnly.Parse(reader.GetString(2),CultureInfo.InvariantCulture),reader.IsDBNull(3)?null:DateOnly.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.GetInt32(10)==1,reader.GetString(11)));
        return headers.Select(h=>
        {
            var holders=new List<EquityHolder>();using var command=Command("SELECT account,shares,percent FROM equity_holder WHERE disclosure_id=$id ORDER BY account",("$id",h.Id));using var reader=command.ExecuteReader();
            while(reader.Read())holders.Add(new(reader.GetString(0),decimal.Parse(reader.GetString(1),CultureInfo.InvariantCulture),reader.IsDBNull(2)?null:decimal.Parse(reader.GetString(2),CultureInfo.InvariantCulture)));
            return new EquityDisclosure(h.Id,key.Market,key.Ticker,h.Date,h.Report,h.Published,h.State,h.Url,h.Hash,h.Document,h.Page,h.Reviewer,h.Comparable,h.Nature,holders);
        }).ToList();
    }
    public void ReviewEquity(EquityReview review,byte[] document,Guid? commandId=null)
    {
        if(Committed(commandId))return;
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        var identified=InstitutionAccounts.Group(review.Account);var group=review.Institution??identified;
        if(!review.OriginalConfirmed || InstitutionAccounts.CandidateGroup(review.Account) is null || group is not ("汇金投资" or "汇金资管" or "证金公司" or "社保基金" or "养老保险基金") || identified is not null && identified!=group || review.ReportAsOf>review.Published || review.Published>today || review.Nature is not ("Strategic" or "Portfolio") || review.ShareClass!="A" || review.PercentBasis!="TotalShares" || string.IsNullOrWhiteSpace(review.Reviewer)||review.Reviewer.Length>100||string.IsNullOrWhiteSpace(review.Page)||review.Page.Length>100)
            throw new InvalidDataException("核验信息不完整：报告期、公告日、原文页码、审核人及持仓性质必须有效。当前适配器仅支持A股/总股本口径。");
        var uri=new Uri(review.DocumentUrl);if(!OfficialEquityDocuments.IsAllowed(uri) || document.Length<5 || !document.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new InvalidDataException("必须取得官方PDF原文，不能仅凭网页确认持仓。");
        ValidateEvidence();
        var market=Scalar("SELECT market FROM equity_disclosure WHERE id=$id AND state IN ('NeedsReview','Accepted')",("$id",review.Id)) as string??throw new InvalidDataException("候选不存在或已被替代。");
        var ticker=(string)Scalar("SELECT ticker FROM equity_disclosure WHERE id=$id",("$id",review.Id))!;
        if(!OfficialEquityDocuments.Matches(uri,market,ticker,review.ReportAsOf))throw new InvalidDataException("官方原文与证券或报告期不匹配。");
        var row=ReadEquityDisclosures(new(market,ticker)).Single(x=>x.Id==review.Id);
        if(row.State=="Critical")throw new InvalidDataException("候选证据损坏，不能审核。");
        var documentHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document)).ToLowerInvariant();
        if(OfficialEquityDocuments.ReviewedIssuerHash(uri) is {} expectedHash&&documentHash!=expectedHash)throw new InvalidDataException("发行人原文已改变，不得继承已确认黄金样本的核验结论。");
        var acceptedHash=Scalar("SELECT f.sha256 FROM equity_disclosure d JOIN source_fetch f ON f.fetch_id=d.document_fetch_id WHERE d.id=$id",("$id",review.Id)) as string;
        if(acceptedHash is not null&&!acceptedHash.Equals(documentHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("同一披露的核验原文不能替换；更正报告须登记新候选并重新逐账户核验。");
        if(!row.Holders.Any(x=>x.Account==review.Account) || row.ReportAsOf is {} known && known!=review.ReportAsOf || row.Published is {} publication && publication!=review.Published)throw new InvalidDataException("审核账户或已确认报告时点不匹配。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var hash=raw.Save(document);var fetch=RecordFetch(hash,uri.AbsoluteUri,hash,"ReviewedEquityDisclosurePdf","manual-pdf-review-v1");
        Execute("UPDATE equity_disclosure SET state='Superseded' WHERE market=$m AND ticker=$t AND report_date=$d AND state='Accepted'",("$m",market),("$t",ticker),("$d",review.ReportAsOf.ToString("yyyy-MM-dd")));
        Execute("UPDATE equity_disclosure SET report_date=$d,published=$p,document_fetch_id=$f,page=$page,reviewer=$who,comparable=$c,nature=$n,share_class=$class,basis=$basis,state='Accepted',verified_at=$now WHERE id=$id",("$d",review.ReportAsOf.ToString("yyyy-MM-dd")),("$p",review.Published.ToString("yyyy-MM-dd")),("$f",fetch),("$page",review.Page),("$who",review.Reviewer),("$c",review.Comparable?1:0),("$n",review.Nature),("$class",review.ShareClass),("$basis",review.PercentBasis),("$now",Now),("$id",review.Id));
        Execute("INSERT INTO equity_account_review VALUES($id,$a,$n) ON CONFLICT(disclosure_id,account) DO UPDATE SET nature=excluded.nature",("$id",review.Id),("$a",review.Account),("$n",review.Nature));
        Execute("INSERT INTO equity_account_identity VALUES($id,$a,$g,$who) ON CONFLICT(disclosure_id,account) DO UPDATE SET institution=excluded.institution,reviewer=excluded.reviewer",("$id",review.Id),("$a",review.Account),("$g",group),("$who",review.Reviewer));
        Execute("INSERT INTO equity_account_comparability VALUES($id,$a,$c) ON CONFLICT(disclosure_id,account) DO UPDATE SET comparable=excluded.comparable",("$id",review.Id),("$a",review.Account),("$c",review.Comparable?1:0));
        Execute("INSERT INTO equity_account_review_audit VALUES($id,$d,$a,$g,$n,$c,$who,$now,$h,$p)",("$id",Guid.NewGuid().ToString("N")),("$d",review.Id),("$a",review.Account),("$g",group),("$n",review.Nature),("$c",review.Comparable?1:0),("$who",review.Reviewer),("$now",Now),("$h",hash),("$p",review.Page));
        Execute("INSERT INTO equity_review_audit VALUES($id,$d,$who,$now,$f,$r,$p,$page)",("$id",Guid.NewGuid().ToString("N")),("$d",review.Id),("$who",review.Reviewer),("$now",Now),("$f",fetch),("$r",review.ReportAsOf.ToString("yyyy-MM-dd")),("$p",review.Published.ToString("yyyy-MM-dd")),("$page",review.Page));
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));RecordCommand(commandId,"equity-review");transaction.Commit();
    }
    public List<EquityHoldingRow> EquityHoldings(string? search,string? institution,string? nature,bool favorites)
    {
        if(search?.Length>200 || institution?.Length>100)throw new InvalidDataException("筛选条件过长。");
        ValidateEvidence();var result=new List<EquityHoldingRow>();
        foreach(var watch in EquityWatchlist().Where(x=>x.Active && (!favorites||x.Favorite)))
        {
            var disclosures=ReadEquityDisclosures(new(watch.Market,watch.Ticker));
            var accepted=disclosures.Where(x=>x.State is "Accepted" or "Critical" && x.ReportAsOf.HasValue).OrderByDescending(x=>x.ReportAsOf).ToArray();
            var current=accepted.FirstOrDefault();var prior=accepted.Skip(1).FirstOrDefault();
            if(current is not null)
            {
                result.AddRange(EquityPeriodRows(watch,current,prior));
            }
            // 候选单列；来源日期不得冒充报告期或用作可比增减持。
            foreach(var candidate in disclosures.Where(x=>x.State=="NeedsReview"||x.State=="Critical"&&x.ReportAsOf is null).GroupBy(x=>x.SourceDate).Select(g=>g.First()))
                foreach(var holder in candidate.Holders.Where(x=>InstitutionAccounts.CandidateGroup(x.Account) is not null))
                    result.Add(new(candidate.Id,watch.Market,watch.Ticker,watch.Name,InstitutionAccounts.CandidateGroup(holder.Account)!,holder.Account,candidate.State=="Critical"?null:holder.Shares,candidate.State=="Critical"?null:holder.Percent,null,null,candidate.SourceDate,candidate.State=="Critical"?"Critical":disclosures.Count(x=>x.SourceDate==candidate.SourceDate&&x.State=="NeedsReview")>1?"Conflict":"NeedsReview",null,"Unknown",watch.Favorite,candidate.SourceUrl));
        }
        return result.Where(x=>(string.IsNullOrWhiteSpace(institution)||x.Institution==institution)&&(string.IsNullOrWhiteSpace(nature)||x.Nature==nature)
            &&(string.IsNullOrWhiteSpace(search)||$"{x.Market} {x.Ticker} {x.Name} {x.Account}".Contains(search.Trim(),StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x=>x.Published??x.SourceDate).ThenBy(x=>x.Ticker).ThenBy(x=>x.Account).ToList();
    }

    /// <summary>按有效报告期重建历次变动；被更正版本不重复计入，缺失榜单从不补零。</summary>
    public List<EquityHoldingRow> EquityEvents(EtfKey key)
    {
        ValidateEvidence();var watch=EquityWatchlist().SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        if(watch is null)return [];
        var periods=ReadEquityDisclosures(key).Where(x=>x.State is "Accepted" or "Critical" && x.ReportAsOf.HasValue).OrderBy(x=>x.ReportAsOf).ToArray();
        return periods.SelectMany((period,i)=>EquityPeriodRows(watch,period,i==0?null:periods[i-1])).OrderByDescending(x=>x.ReportAsOf).ThenBy(x=>x.Account).ToList();
    }
    private IEnumerable<EquityHoldingRow> EquityPeriodRows(EquityWatch watch,EquityDisclosure current,EquityDisclosure? prior)
    {
        foreach(var account in current.Holders.Concat(prior?.Holders??[]).Select(x=>x.Account).Where(x=>InstitutionAccounts.CandidateGroup(x) is not null).Distinct())
        {
            var value=current.Holders.SingleOrDefault(x=>x.Account==account);var before=prior?.Holders.SingleOrDefault(x=>x.Account==account);
            // 每个账户独立核验；缺席只证明本期完整表中未列示，不能推断清仓。
            var identityPeriod=value is null?prior:current;
            var accountNature=Scalar("SELECT nature FROM equity_account_review WHERE disclosure_id=$id AND account=$a",("$id",identityPeriod?.Id),("$a",account)) as string;
            var identity=Scalar("SELECT institution FROM equity_account_identity WHERE disclosure_id=$id AND account=$a",("$id",identityPeriod?.Id),("$a",account)) as string??InstitutionAccounts.Group(account)??"主体待核验";
            bool Comparable(EquityDisclosure period)=>Convert.ToInt32(Scalar("SELECT comparable FROM equity_account_comparability WHERE disclosure_id=$id AND account=$a",("$id",period.Id),("$a",account))??0)==1;
            var previousReviewed=prior is not null && Scalar("SELECT nature FROM equity_account_review WHERE disclosure_id=$id AND account=$a",("$id",prior.Id),("$a",account)) is not null;
            var previousIdentity=prior is null?null:Scalar("SELECT institution FROM equity_account_identity WHERE disclosure_id=$id AND account=$a",("$id",prior.Id),("$a",account)) as string;
            var comparison=prior?.State=="Critical" || before is not null&&(!previousReviewed||previousIdentity!=identity)
                ?(State:"NotComparable",Delta:(decimal?)null)
                :DirectEquityComparison.Compare(before?.Shares,value?.Shares,Comparable(current)&&prior is not null&&Comparable(prior),prior?.State=="Accepted");
            var state=current.State=="Critical"?"Critical":accountNature is null||identity=="主体待核验"?"NeedsReview":comparison.State;
            // 页码属于逐账户核验；不能继承该披露最后一个审核账户的证据位置。
            var evidencePage=Scalar("SELECT page FROM equity_account_review_audit WHERE disclosure_id=$id AND account=$a ORDER BY reviewed_at DESC,rowid DESC LIMIT 1",("$id",current.Id),("$a",account)) as string;
            var evidenceHash=Scalar("SELECT document_hash FROM equity_account_review_audit WHERE disclosure_id=$id AND account=$a ORDER BY reviewed_at DESC,rowid DESC LIMIT 1",("$id",current.Id),("$a",account)) as string;
            yield return new(current.Id,watch.Market,watch.Ticker,watch.Name,identity,account,state=="Critical"?null:value?.Shares,state=="Critical"?null:value?.Percent,current.ReportAsOf,current.Published,current.SourceDate,state,state is "Critical" or "NeedsReview"?null:comparison.Delta,accountNature??"Unknown",watch.Favorite,current.DocumentUrl??current.SourceUrl,prior?.ReportAsOf,evidencePage,evidenceHash);
        }
    }
}
