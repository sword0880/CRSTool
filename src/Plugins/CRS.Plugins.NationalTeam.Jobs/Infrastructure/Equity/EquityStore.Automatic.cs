using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EquityStore
{
    private static readonly string[] AutomaticTables=["equity_auto_security","equity_auto_state","equity_auto_document","equity_auto_page"];
    private void InitializeAutomaticEquitySchema()
    {
        var version=Scalar("SELECT version FROM feature_schema_migration WHERE feature='automatic-equity'");
        var present=AutomaticTables.Count(name=>Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n",("$n",name)))==1);
        if(version is not null){if(Convert.ToInt32(version)!=1||present!=AutomaticTables.Length)throw new InvalidDataException("自动持股状态库版本或表不完整。");return;}
        if(present!=0)throw new InvalidDataException("自动持股状态表缺少迁移身份，拒绝重建覆盖。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE equity_auto_security(ticker TEXT PRIMARY KEY,market TEXT NOT NULL,name TEXT NOT NULL,org_id TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id));
            CREATE TABLE equity_auto_state(id INTEGER PRIMARY KEY CHECK(id=1),cursor TEXT,updated TEXT NOT NULL,last_issue TEXT);
            CREATE TABLE equity_auto_document(id TEXT PRIMARY KEY,metadata TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),state TEXT NOT NULL,original_hash TEXT,extract_hash TEXT,disclosure_id TEXT,issue TEXT,updated TEXT NOT NULL);
            CREATE TABLE equity_auto_page(window TEXT NOT NULL,date TEXT NOT NULL,page INTEGER NOT NULL,total INTEGER NOT NULL,ids TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(window,date,page));
            CREATE INDEX equity_auto_document_state ON equity_auto_document(state,updated);
            INSERT INTO equity_auto_state VALUES(1,NULL,strftime('%Y-%m-%dT%H:%M:%fZ','now'),NULL);
            """);
        Execute("INSERT INTO feature_schema_migration VALUES('automatic-equity',1,$now)",("$now",Now));transaction.Commit();
    }
    private AutomaticEquityCursor? AutomaticCursor()
    {
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_auto_state"))!=1)throw new InvalidDataException("自动持股水位遗失，拒绝当作首次运行。");
        var json=Scalar("SELECT cursor FROM equity_auto_state WHERE id=1") as string;
        if(json is null)return null;
        var cursor=JsonSerializer.Deserialize<AutomaticEquityCursor>(json)??throw new InvalidDataException("自动持股水位损坏。");
        if(cursor.From>cursor.Through||cursor.Through.DayNumber-cursor.From.DayNumber>60||cursor.Page is <1 or >1000
            ||cursor.Complete!=(cursor.Date is null)||cursor.Date is {} date&&(date<cursor.From||date>cursor.Through))
            throw new InvalidDataException("自动持股日期或分页水位无效。");
        return cursor;
    }
    private void SaveAutomaticCursor(AutomaticEquityCursor value,string? issue=null)=>Execute("UPDATE equity_auto_state SET cursor=$c,updated=$n,last_issue=$i WHERE id=1",
        ("$c",JsonSerializer.Serialize(value)),("$n",Now),("$i",issue));
    public AutomaticEquityStatus AutomaticEquityStatus()
    {
        var counts=new Dictionary<string,int>(StringComparer.Ordinal);using(var command=Command("SELECT state,count(*) FROM equity_auto_document GROUP BY state"))
        using(var reader=command.ExecuteReader())while(reader.Read())counts.Add(reader.GetString(0),reader.GetInt32(1));
        return new(AutomaticCursor(),Convert.ToInt32(Scalar("SELECT count(*) FROM equity_auto_security")),counts.Values.Sum(),
            counts.GetValueOrDefault("Queued"),counts.GetValueOrDefault("Verified")+counts.GetValueOrDefault("VerifiedWithExceptions"),counts.GetValueOrDefault("NoMatch"),counts.GetValueOrDefault("NeedsReview")+counts.GetValueOrDefault("VerifiedWithExceptions"),
            CRS.Timing.BeijingClock.Parse((string)Scalar("SELECT updated FROM equity_auto_state WHERE id=1")!),
            Scalar("SELECT last_issue FROM equity_auto_state WHERE id=1") as string,
            Scalar("SELECT max(f.retrieved_utc) FROM equity_auto_security s JOIN source_fetch f ON f.fetch_id=s.fetch_id") is string fetched?CRS.Timing.BeijingClock.Parse(fetched):null);
    }
    public List<AutomaticEquityDocument> PendingAutomaticEquityDocuments(int count)
    {
        if(count is <1 or >20)throw new ArgumentOutOfRangeException(nameof(count));
        using var command=Command("SELECT metadata FROM equity_auto_document WHERE state='Queued' ORDER BY updated,id LIMIT $n",("$n",count));
        using var reader=command.ExecuteReader();var rows=new List<AutomaticEquityDocument>();
        while(reader.Read())rows.Add(JsonSerializer.Deserialize<AutomaticEquityDocument>(reader.GetString(0))??throw new InvalidDataException("自动持股队列身份损坏。"));
        return rows;
    }
    public void SaveAutomaticEquitySecurities(byte[] original)
    {
        var securities=AutomaticEquitySource.ParseSecurities(original);beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var hash=raw.Save(original);var fetch=RecordFetch(hash,AutomaticEquitySource.SecuritiesUri.AbsoluteUri,hash,"OfficialCninfoSecurityDirectory","cninfo-security-directory-v1");
        foreach(var security in securities)
        {
            operationCancellation.ThrowIfCancellationRequested();
            Execute("INSERT INTO equity_auto_security VALUES($t,$m,$n,$o,$f) ON CONFLICT(ticker) DO UPDATE SET market=excluded.market,name=excluded.name,org_id=excluded.org_id,fetch_id=excluded.fetch_id",
                ("$t",security.Ticker),("$m",security.Market),("$n",security.Name),("$o",security.OrgId),("$f",fetch));
        }
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    public void BeginAutomaticEquityWindow(DateOnly through)
    {
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime);
        if(through>=today||through<today.AddDays(-60))throw new InvalidDataException("自动披露发现只查询已结束的北京时间日期。");
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_auto_security"))<100)throw new InvalidDataException("尚未取得完整官方证券身份目录。");
        var previous=AutomaticCursor();if(previous is {Complete:false}||previous is {Complete:true}&&previous.Through>=through)return;
        // 每日重读最近七日，发现补登；报告ID去重，不覆盖既有金融事实。
        var from=previous is null?through.AddDays(-44):previous.Through.AddDays(-6);
        if(from<through.AddDays(-44))from=through.AddDays(-44);
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        SaveAutomaticCursor(new(from,through,through,1,null,false));operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    public void SaveAutomaticEquityPage(byte[] original)
    {
        var cursor=AutomaticCursor()??throw new InvalidDataException("自动发现水位尚未建立。");
        if(cursor.Date is not {} date)throw new InvalidDataException("当前窗口已结束，不能追加旧分页。");
        var securities=new Dictionary<string,AutomaticEquitySecurity>(StringComparer.Ordinal);
        using(var command=Command("SELECT market,ticker,name,org_id FROM equity_auto_security"))
        using(var reader=command.ExecuteReader())while(reader.Read())securities.Add(reader.GetString(1),new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));
        var page=AutomaticEquitySource.ParsePage(date,cursor.Page,original,securities);
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var hash=raw.Save(original);var fetch=RecordFetch(hash,AutomaticEquitySource.ReportsUri.AbsoluteUri,hash,"OfficialCninfoAnnouncementIndex","cninfo-automatic-equity-v1");
        var window=cursor.From+"/"+cursor.Through;
        if(cursor.DayTotal is {} expected&&expected!=page.Total){RestartAutomaticDay(cursor,window,"目录总数变化，保留原件后重读当日");transaction.Commit();return;}
        foreach(var document in page.Documents)
        {
            operationCancellation.ThrowIfCancellationRequested();var json=JsonSerializer.Serialize(document);
            var existing=Scalar("SELECT metadata FROM equity_auto_document WHERE id=$id",("$id",document.Id)) as string;
            if(existing is not null)
            {
                var prior=JsonSerializer.Deserialize<AutomaticEquityDocument>(existing)??throw new InvalidDataException("既有公告身份损坏。");
                if(prior!=document with{Name=prior.Name})throw new InvalidDataException("同公告ID的证券、报告期或原文身份已改变，不能静默替换。");
            }
            Execute("INSERT OR IGNORE INTO equity_auto_document VALUES($id,$m,$f,'Queued',NULL,NULL,NULL,NULL,$n)",("$id",document.Id),("$m",json),("$f",fetch),("$n",Now));
        }
        Execute("INSERT INTO equity_auto_page VALUES($w,$d,$p,$t,$ids,$f) ON CONFLICT(window,date,page) DO UPDATE SET total=excluded.total,ids=excluded.ids,fetch_id=excluded.fetch_id",
            ("$w",window),("$d",date.ToString("yyyy-MM-dd")),("$p",cursor.Page),("$t",page.Total),("$ids",JsonSerializer.Serialize(page.Ids)),("$f",fetch));
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$f",("$f",fetch));
        if(cursor.Page*AutomaticEquitySource.PageSize<page.Total)SaveAutomaticCursor(cursor with{Page=cursor.Page+1,DayTotal=page.Total});
        else FinishAutomaticDay(cursor,window,page.Total);
        operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    private void RestartAutomaticDay(AutomaticEquityCursor cursor,string window,string reason)
    {
        Execute("DELETE FROM equity_auto_page WHERE window=$w AND date=$d",("$w",window),("$d",cursor.Date!.Value.ToString("yyyy-MM-dd")));
        SaveAutomaticCursor(cursor with{Page=1,DayTotal=null},reason);
    }
    private void FinishAutomaticDay(AutomaticEquityCursor cursor,string window,int total)
    {
        var ids=new List<string>();using(var command=Command("SELECT ids FROM equity_auto_page WHERE window=$w AND date=$d ORDER BY page",("$w",window),("$d",cursor.Date!.Value.ToString("yyyy-MM-dd"))))
        using(var reader=command.ExecuteReader())while(reader.Read())ids.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(0))!);
        if(ids.Count!=total||ids.Distinct(StringComparer.Ordinal).Count()!=total){RestartAutomaticDay(cursor,window,"目录分页重叠或缺失，保留原件后重读当日");return;}
        var next=cursor.Date!.Value.AddDays(-1);SaveAutomaticCursor(cursor with{Date=next<cursor.From?null:next,Page=1,DayTotal=null,Complete=next<cursor.From});
    }
    public void RecordAutomaticEquityIssue(string? id,string reason,byte[]? original=null)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>500)throw new InvalidDataException("自动核验异常原因无效。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();string? hash=null;
        if(original is not null)hash=raw.Save(original);
        if(id is not null)Execute("UPDATE equity_auto_document SET state='NeedsReview',issue=$i,original_hash=coalesce($h,original_hash),updated=$n WHERE id=$id AND state='Queued'",
            ("$id",id),("$i",reason),("$h",hash),("$n",Now));
        Execute("UPDATE equity_auto_state SET last_issue=$i,updated=$n WHERE id=1",("$i",reason),("$n",Now));transaction.Commit();
    }
}
