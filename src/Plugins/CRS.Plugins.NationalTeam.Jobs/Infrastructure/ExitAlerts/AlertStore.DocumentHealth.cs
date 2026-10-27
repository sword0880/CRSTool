using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class AlertStore
{
    private sealed record DocumentReference(string Market,string Ticker,string Url,string Hash,DateTimeOffset? FirstSeenAt=null);
    private static DocumentReference Reference(AlertFundMetadata value)=>new(value.Market,value.Ticker,value.EvidenceUrl,value.EvidenceHash);
    private static DocumentReference Reference(NamedEtfHolding value)=>new(value.Market,value.Ticker,value.DocumentUrl,value.DocumentHash,value.FirstSeenAt);
    private static DocumentReference Reference(AlertDocument value)=>new(value.Market,value.Ticker,value.Url,value.Hash);

    /// <summary>同次操作批量绑定证券、原件、来源版本及未解除Q15；不缓存到下次操作，不修复或写入任何审核状态。</summary>
    private Dictionary<DocumentReference,AlertDocument?> InspectDocuments(IEnumerable<DocumentReference> references,bool verifyRaw=true)
    {
        var requested=references.Distinct().ToArray();
        if(requested.Length>10000)throw new InvalidDataException("ETF原件引用超过读取限额。");
        var result=requested.ToDictionary(x=>x,_=>(AlertDocument?)null);
        foreach(var batch in requested.Chunk(128))
        {
            session.OperationCancellation.ThrowIfCancellationRequested();
            var args=new List<(string,object?)>();var values=new List<string>();
            for(var i=0;i<batch.Length;i++)
            {
                values.Add($"({i},$m{i},$t{i},$u{i},$h{i})");
                args.Add(($"$m{i}",batch[i].Market));args.Add(($"$t{i}",batch[i].Ticker));args.Add(($"$u{i}",batch[i].Url));args.Add(($"$h{i}",batch[i].Hash));
            }
            using var command=Command("""
                WITH requested(i,market,ticker,url,hash) AS (VALUES
                """+string.Join(',',values)+"""
                ) SELECT r.i,d.id,d.first_seen,
                    CASE WHEN f.source_url=d.url AND f.sha256=d.hash AND f.original_sha256=d.hash
                        AND f.provider='ReviewedEtfAlertPdf' AND f.parser_version='human-etf-proof-v1' AND f.status!='ParseFailed'
                        AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                    THEN 1 ELSE 0 END,f.retrieved_utc
                FROM requested r JOIN nt_alert_document d ON d.market=r.market AND d.ticker=r.ticker AND d.url=r.url AND d.hash=r.hash
                LEFT JOIN source_fetch f ON f.fetch_id=d.fetch_id
                """,args.ToArray());
            var rows=new Dictionary<int,List<(string Id,string FirstSeen,bool Valid,string? Retrieved)>>();var returned=0;
            using(var reader=command.ExecuteReader())while(reader.Read())
            {
                session.OperationCancellation.ThrowIfCancellationRequested();
                if(++returned>10000)throw new InvalidDataException("ETF原件登记匹配超过读取限额。");
                var i=reader.GetInt32(0);
                if(!rows.TryGetValue(i,out var matches))rows[i]=matches=[];
                if(matches.Count<2)matches.Add((reader.GetString(1),reader.GetString(2),reader.GetInt32(3)==1,reader.IsDBNull(4)?null:reader.GetString(4)));
            }
            foreach(var (i,matches) in rows)
            {
                var reference=batch[i];
                if(matches.Count!=1||!matches[0].Valid||!DateTimeOffset.TryParseExact(matches[0].FirstSeen,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var first)
                    ||!DateTimeOffset.TryParseExact(matches[0].Retrieved,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var retrieved)
                    ||retrieved>first||first>clock.GetUtcNow()||reference.FirstSeenAt is {} expected&&first!=expected
                    ||!Uri.TryCreate(reference.Url,UriKind.Absolute,out var uri)||!OfficialEquityDocuments.IsAllowed(uri)||OfficialEquityDocuments.IsReviewedIssuer(uri))continue;
                result[reference]=new(matches[0].Id,reference.Market,reference.Ticker,reference.Url,reference.Hash,matches[0].FirstSeen);
            }
        }
        if(verifyRaw)
        {
            var health=raw.VerifyMany(result.Where(x=>x.Value is not null).Select(x=>x.Key.Hash),session.OperationCancellation);
            foreach(var reference in requested)if(result[reference] is not null&&!health[reference.Hash])result[reference]=null;
        }
        return result;
    }
    private AlertFundMetadata[] ValidateMetadata(IEnumerable<AlertFundMetadata> values)
    {
        var rows=values.ToArray();var health=InspectDocuments(rows.Select(Reference));
        return rows.Select(x=>health[Reference(x)] is not null?x:x with{OriginalConfirmed=false}).ToArray();
    }
    private NamedEtfHolding[] ValidateHoldings(IEnumerable<NamedEtfHolding> values)
    {
        var rows=values.ToArray();var health=InspectDocuments(rows.Select(Reference));
        return rows.Select(x=>health[Reference(x)] is not null?x:x with{OriginalConfirmed=false}).ToArray();
    }
}
