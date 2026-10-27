using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>ETF报告目录独立存储；只共享连接、存证及命令机制，不拥有已批准持仓或预警事务。</summary>
internal sealed class EtfReportIndexStore(IMarketRepositorySession session)
{
    public void InitializeSchema()
    {
        var version=session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='etf-report-index'");
        var tables=Convert.ToInt32(session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_etf_report_search','nt_etf_report_page')"));
        if(version is not null&&Convert.ToInt32(version)!=1||(version is null?tables!=0:tables!=2))throw new InvalidDataException("ETF报告目录迁移与实际表不一致，拒绝自动重建证据。");
        session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();
        session.Execute("""
            CREATE TABLE IF NOT EXISTS nt_etf_report_search(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,from_date TEXT NOT NULL,to_date TEXT NOT NULL,captured_at TEXT NOT NULL,total INTEGER NOT NULL,pages INTEGER NOT NULL,payload TEXT NOT NULL,fingerprint TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nt_etf_report_search_security ON nt_etf_report_search(market,ticker);
            CREATE TABLE IF NOT EXISTS nt_etf_report_page(search_id TEXT NOT NULL REFERENCES nt_etf_report_search(id),page_no INTEGER NOT NULL,request_url TEXT NOT NULL,request_body_hash TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(search_id,page_no));
            """);
        session.Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('etf-report-index',1,$now)",("$now",session.Now));transaction.Commit();
    }
    public bool CommandComplete(Guid id)
    {
        var operation=session.Scalar("SELECT operation FROM worker_business_command WHERE request=$id",("$id",id.ToString("N"))) as string;
        if(operation is not null&&operation!="etf-report-search")throw new InvalidDataException("ETF目录命令身份属于其他操作。");
        return operation is not null;
    }
    public void Save(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,Guid? commandId)
    {
        if(commandId is {} committed&&CommandComplete(committed))return;
        _=OfficialEtfReportIndex.Request(key,from,through,1);
        if(through>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(session.Clock).DateTime))throw new InvalidDataException("ETF报告查询不能跨到未来。");
        if(Convert.ToInt32(session.Scalar("SELECT count(*) FROM watchlist_membership WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker)))!=1)throw new InvalidDataException("ETF不在用户配置目录中。");
        var parsed=ParsePages(key,from,through,pages);var documents=parsed.SelectMany(x=>x.Documents).ToArray();var payload=JsonSerializer.Serialize(documents);
        session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();var id=Guid.NewGuid().ToString("N");
        session.Execute("INSERT INTO nt_etf_report_search VALUES($id,$m,$t,$f,$to,$at,$n,$p,$v,$h)",("$id",id),("$m",key.Market),("$t",key.Ticker),("$f",from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$to",through.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$at",session.Now),("$n",parsed[0].Total),("$p",parsed.Count),("$v",payload),("$h",ContentFingerprint.Sha256(Encoding.UTF8.GetBytes(payload))));
        for(var index=0;index<pages.Count;index++)
        {
            session.OperationCancellation.ThrowIfCancellationRequested();var page=index+1;var hash=session.RawEvidence.Save(pages[index]);var uri=OfficialEtfReportIndex.Request(key,from,through,page);
            var fetch=session.RecordFetch(hash,uri.AbsoluteUri,hash,Provider(key),OfficialEtfReportIndex.ParserVersion);
            session.Execute("INSERT INTO nt_etf_report_page VALUES($s,$p,$u,$b,$f)",("$s",id),("$p",page),("$u",uri.AbsoluteUri),("$b",BodyHash(key,from,through,page)),("$f",fetch));
            session.Execute("UPDATE source_fetch SET status='Parsed' WHERE fetch_id=$f",("$f",fetch));
        }
        // 目录登记结束不能早于各页原件的登记时间；不把写入前的时间当作完成时点。
        session.Execute("UPDATE nt_etf_report_search SET captured_at=$now WHERE id=$id",("$now",session.Now),("$id",id));
        session.RecordCommand(commandId,"etf-report-search");session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    public EtfReportIndexSnapshot Read(EtfKey key)
    {
        _=OfficialEtfReportIndex.Request(key,new(2026,1,1),new(2026,1,1),1);
        string id,payload,fingerprint,captured;DateOnly from,through;int total,count;
        using(var command=session.Command("SELECT id,from_date,to_date,captured_at,total,pages,payload,fingerprint FROM nt_etf_report_search WHERE market=$m AND ticker=$t ORDER BY rowid DESC LIMIT 1",("$m",key.Market),("$t",key.Ticker)))
        using(var reader=command.ExecuteReader())
        {
            if(!reader.Read())return new(key,"NotSearched",null,null,null,null,null,[],EtfReportIndexSnapshot.Version);
            id=reader.GetString(0);from=Date(reader.GetString(1));through=Date(reader.GetString(2));captured=reader.GetString(3);total=reader.GetInt32(4);count=reader.GetInt32(5);payload=reader.GetString(6);fingerprint=reader.GetString(7);
        }
        _=OfficialEtfReportIndex.Request(key,from,through,1);
        if(!ContentFingerprint.Matches(fingerprint,Encoding.UTF8.GetBytes(payload))||total is <0 or >OfficialEtfReportIndex.MaximumDocuments||count is <1 or >40)throw new InvalidDataException("已存ETF目录载荷或分页身份损坏。");
        var documents=JsonSerializer.Deserialize<EtfReportIndexDocument[]>(payload)??throw new InvalidDataException("ETF目录载荷缺失。");
        if(documents.Length!=total||documents.Any(x=>x is null||string.IsNullOrWhiteSpace(x.Title)||string.IsNullOrWhiteSpace(x.DocumentUrl)||x.SecurityCodes is null||!x.SecurityCodes.Contains(key.Ticker)||x.Announced<from||x.Announced>through))throw new InvalidDataException("已存ETF目录行数或字段不符。");
        var references=new List<(int Page,string Url,string Body,string Hash,string Original,string Source,string Provider,string Version,string Status,string At,bool Quarantined)>();
        using(var command=session.Command("""
            SELECT p.page_no,p.request_url,p.request_body_hash,f.sha256,f.original_hash,f.source_url,f.provider,f.parser_version,f.status,f.retrieved_utc,
            EXISTS(SELECT 1 FROM quality_issue q WHERE q.rule_id='Q15' AND q.status='Open' AND q.evidence_id=f.fetch_id)
            FROM nt_etf_report_page p JOIN source_fetch f ON f.fetch_id=p.fetch_id WHERE p.search_id=$s ORDER BY p.page_no
            """,("$s",id)))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {session.CheckReadLimit(references.Count+1);references.Add((reader.GetInt32(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetInt32(10)!=0));}
        var now=CRS.Timing.BeijingClock.Read(session.Clock);
        var validTime=DateTimeOffset.TryParseExact(captured,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var capturedAt)&&capturedAt<=now;
        var healthy=references.Count==count&&validTime;
        var pages=new List<byte[]>();
        foreach(var item in references)
        {
            session.OperationCancellation.ThrowIfCancellationRequested();
            if(item.Page!=pages.Count+1||item.Page>count||item.Url!=OfficialEtfReportIndex.Request(key,from,through,item.Page).AbsoluteUri||item.Source!=item.Url||item.Body!=BodyHash(key,from,through,item.Page)
                ||item.Provider!=Provider(key)||item.Version!=OfficialEtfReportIndex.ParserVersion||item.Status!="Parsed"||item.Hash!=item.Original||item.Quarantined
                ||!DateTimeOffset.TryParseExact(item.At,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var at)||at>capturedAt)
            {healthy=false;break;}
            try{pages.Add(session.RawEvidence.ReadVerified(item.Hash,session.OperationCancellation));}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){healthy=false;break;}
        }
        if(healthy)
        {
            try
            {
                var parsed=ParsePages(key,from,through,pages);
                healthy=parsed[0].Total==total&&JsonSerializer.Serialize(parsed.SelectMany(x=>x.Documents).ToArray())==payload;
            }
            catch(InvalidDataException){healthy=false;}
        }
        return new(key,healthy?"IndexedUnreviewed":"EvidenceUnavailable",id,from,through,captured,total,Array.AsReadOnly(documents),EtfReportIndexSnapshot.Version);
    }
    private IReadOnlyList<EtfReportIndexPage> ParsePages(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages)
        =>OfficialEtfReportIndex.ParseCapture(key,from,through,pages,session.OperationCancellation);
    private static string Provider(EtfKey key)=>key.Market=="SSE"?"OfficialSseEtfReportIndex":"OfficialSzseEtfReportIndex";
    private static string BodyHash(EtfKey key,DateOnly from,DateOnly through,int page)=>ContentFingerprint.Sha256(OfficialEtfReportIndex.RequestBody(key,from,through,page)??[]);
    private static DateOnly Date(string value)=>DateOnly.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture);
}
