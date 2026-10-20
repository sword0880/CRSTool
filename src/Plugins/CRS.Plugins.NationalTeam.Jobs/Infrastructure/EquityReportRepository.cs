using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    public bool EquityReportCommandComplete(Guid id)=>Committed(id);
    private void InitializeEquityReportSchema()=>Execute("""
        CREATE TABLE IF NOT EXISTS equity_report_search(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,from_date TEXT NOT NULL,to_date TEXT NOT NULL,completed_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS equity_report_index(search_id TEXT NOT NULL REFERENCES equity_report_search(id),document_url TEXT NOT NULL,bulletin_id TEXT NOT NULL,title TEXT NOT NULL,announced TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(search_id,document_url));
        """);
    public void SaveEquityReports(EtfKey key,DateOnly from,DateOnly to,IReadOnlyList<byte[]> pages,Guid? commandId=null)
    {
        if(Committed(commandId))return;
        if(pages.Count is <1 or >40)throw new InvalidDataException("公告检索分页数量异常。");
        var parsed=pages.Select((bytes,i)=>OfficialEquityReports.Parse(key,from,to,i+1,bytes)).ToArray();
        var first=parsed[0];
        if(pages.Count!=first.Pages||parsed.Any(x=>x.Pages!=first.Pages||x.Total!=first.Total)||parsed.Sum(x=>x.GroupIds.Count)!=first.Total||parsed.SelectMany(x=>x.GroupIds).Distinct().Count()!=first.Total||parsed.SelectMany(x=>x.Documents).Select(x=>x.DocumentUrl).Distinct().Count()!=parsed.Sum(x=>x.Documents.Count))throw new InvalidDataException("公告检索跨页重复、变化或不完整。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var search=Guid.NewGuid().ToString("N");
        Execute("INSERT INTO equity_report_search VALUES($id,$m,$t,$f,$to,$now)",("$id",search),("$m",key.Market),("$t",key.Ticker),("$f",from.ToString("yyyy-MM-dd")),("$to",to.ToString("yyyy-MM-dd")),("$now",Now));
        for(var i=0;i<pages.Count;i++)
        {
            var hash=raw.Save(pages[i]);var fetch=RecordFetch(hash,OfficialEquityReports.Request(key,from,to,i+1).AbsoluteUri,hash,"OfficialSseReportIndex","sse-company-bulletin-v1");
            foreach(var document in parsed[i].Documents)
                Execute("INSERT INTO equity_report_index VALUES($s,$u,$b,$title,$d,$f)",("$s",search),("$u",document.DocumentUrl),("$b",document.BulletinId),("$title",document.Title),("$d",document.Announced.ToString("yyyy-MM-dd")),("$f",fetch));
            Execute("UPDATE source_fetch SET status='Parsed' WHERE fetch_id=$f",("$f",fetch));
        }
        RecordCommand(commandId,"equity-report-search");transaction.Commit();
    }
    public List<EquityReportDocument> EquityReports(EtfKey key)
    {
        ValidateEvidence();var result=new List<EquityReportDocument>();
        using var command=Command("""
            SELECT s.id,i.bulletin_id,i.title,i.announced,i.document_url,f.source_url,
            CASE WHEN EXISTS(SELECT 1 FROM quality_issue q WHERE q.rule_id='Q15' AND q.status='Open' AND q.evidence_id=i.fetch_id) THEN 'Critical' ELSE 'Indexed' END
            FROM equity_report_index i JOIN equity_report_search s ON s.id=i.search_id JOIN source_fetch f ON f.fetch_id=i.fetch_id
            WHERE s.market=$m AND s.ticker=$t ORDER BY i.announced DESC,s.completed_at DESC,s.id DESC
            """,("$m",key.Market),("$t",key.Ticker));
        using var reader=command.ExecuteReader();var urls=new HashSet<string>();
        while(reader.Read())if(urls.Add(reader.GetString(4)))result.Add(new(reader.GetString(0),key.Market,key.Ticker,reader.GetString(1),reader.GetString(2),DateOnly.ParseExact(reader.GetString(3),"yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetString(4),reader.GetString(6),reader.GetString(5)));
        return result;
    }
}
