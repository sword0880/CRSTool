using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class NavStore
{
    internal (NavResearchDataset Dataset,IReadOnlyList<NavRevision> Revisions) ResearchSnapshot(EtfKey key,DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var revisions=NavRevisions(key,from,through,cutoff);var accepted=AcceptedNavPoints(revisions);
        var captures=accepted.Select(x=>x.CaptureId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var sources=new List<NavResearchSource>();var byCapture=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var id in captures)
        {
            var hashes=new List<string>();
            using var command=_session.Command("""
                SELECT f.source_url,f.sha256,f.retrieved_utc FROM nt_nav_capture_document d
                JOIN source_fetch f ON f.fetch_id=d.fetch_id WHERE d.capture_id=$id ORDER BY f.source_url,f.sha256
                """,("$id",id));
            using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                _session.CheckReadLimit(sources.Count+1);
                var hash=reader.GetString(1);hashes.Add(hash);
                // 旧存储未记录原件长度，保持未知；健康已由NavRevisions重新核验，不伪造字节数。
                sources.Add(new(reader.GetString(0),hash,DateTimeOffset.Parse(reader.GetString(2),CultureInfo.InvariantCulture),null));
            }
            byCapture.Add(id,hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        var points=accepted.Select(x=>new NavResearchPoint(x.Date,x.UnitNavYuan,x.AccumulatedNavYuan,x.FirstSeenAt,byCapture[x.CaptureId])).ToArray();
        var ordered=sources.Distinct().OrderBy(x=>x.Url,StringComparer.Ordinal).ThenBy(x=>x.Sha256,StringComparer.Ordinal).ThenBy(x=>x.RetrievedAt).ToArray();
        var fingerprint=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{Version="stored-nav-research-v1",key,from,through,Cutoff=cutoff.ToUniversalTime(),
            Revisions=revisions.OrderBy(x=>x.Date).ThenBy(x=>x.Revision),Sources=ordered,Points=points}));
        return(new(key.Market,key.Ticker,"stored-nav-research-v1",cutoff,fingerprint,ordered,points),revisions);
    }
}
