using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class MarketActivityStore
{
    private (ActivityRegistration Value,string ProofHash,string Head) ReadActivityRegistration(string id,ActivityProof proof)
    {
        using var command=_session.Command("SELECT registered_at,origin,proof_json,proof_hash,head_hash FROM market_activity_registration WHERE fetch_id=$id",("$id",id));using var reader=command.ExecuteReader();
        if(!reader.Read()||reader.IsDBNull(4))throw new InvalidDataException("成交首次登记或事件链头缺失，不能返回假空历史。");
        ActivityRegistration registration;
        try{registration=JsonSerializer.Deserialize<ActivityRegistration>(reader.GetString(2))??throw new JsonException();}
        catch(JsonException error){throw new InvalidDataException("成交登记证明损坏。",error);}
        if(Hash(reader.GetString(2))!=reader.GetString(3)||registration.Proof!=proof||registration.FirstSeenAt!=ActivityInstant(reader.GetString(0))
            ||registration.Origin!=reader.GetString(1)||registration.Origin is not ("Native" or "LegacyObservedNow")||registration.FirstSeenAt<proof.RetrievedAt
            ||registration.InitialDecision is not ("Observed" or "Accepted" or "Rejected"))
            throw new InvalidDataException("成交金融值、原件元数据或登记时点与冻结证明不一致。");
        return(registration,reader.GetString(3),reader.GetString(4));
    }
    private List<ActivityJournalEvent> ReadActivityJournal(string id,ActivityRegistration registration,string root,string head)
    {
        var events=new List<ActivityJournalEvent>();
        using(var command=_session.Command("SELECT decision,recorded_at,actor,cause,command_id,previous_hash,event_hash FROM market_activity_state_event WHERE fetch_id=$id ORDER BY sequence",("$id",id)))using(var reader=command.ExecuteReader())
        while(reader.Read())
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();var value=new ActivityJournalEvent(reader.GetString(0),ActivityInstant(reader.GetString(1)),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),reader.GetString(6));
            if(value.Previous!=(events.LastOrDefault()?.Hash??root)||value.Hash!=EventHash(id,value)||value.Decision is not ("Observed" or "Accepted" or "Rejected")
                ||value.At<registration.FirstSeenAt||events.LastOrDefault() is {} previous&&previous.At>value.At)
                throw new InvalidDataException("成交审核事件链损坏或时间倒退。");
            events.Add(value);_session.CheckReadLimit(events.Count);
        }
        if(events.Count==0||events[0].At!=registration.FirstSeenAt||events[0].Decision!=registration.InitialDecision||events[0].Cause!=(registration.Origin=="Native"?"SourceObserved":"LegacyBaselineAtMigration")||events[^1].Hash!=head)
            throw new InvalidDataException("成交审核事件或登记基线不完整。");
        using(var command=_session.Command("SELECT decision,reviewer,reviewed_at FROM market_activity_review WHERE fetch_id=$id",("$id",id)))using(var reader=command.ExecuteReader())
        {
            var exists=reader.Read();var current=exists?reader.GetString(0):"Observed";var last=events[^1];
            if(current!=last.Decision||last.Cause is "ExplicitReview" or "CompetingValueRejected"&&(!exists||reader.GetString(1)!=last.Actor||ActivityInstant(reader.GetString(2))!=last.At))
                throw new InvalidDataException("成交当前审核指针与事件历史不一致。");
        }
        return events;
    }
    public MarketActivityKnowledgeReport MarketActivityKnowledge(DateOnly from,DateOnly through,DateTimeOffset? requestedCutoff)
    {
        try{return ReadActivityKnowledge(from,through,requestedCutoff);}
        catch(Exception error) when(error is JsonException or FormatException or OverflowException)
        {throw new InvalidDataException("成交知识登记含无法解析的时点或金融数值。",error);}
    }
    private MarketActivityKnowledgeReport ReadActivityKnowledge(DateOnly from,DateOnly through,DateTimeOffset? requestedCutoff)
    {
        var cutoff=requestedCutoff??_session.Clock.GetUtcNow();
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>_session.Clock.GetUtcNow()||through>DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("成交知识查询的区间或截止时刻无效。");
        if(Convert.ToInt32(_session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='market-activity-knowledge'"))!=1)throw new InvalidDataException("成交知识时间链版本缺失。");
        var ids=new List<string>();using(var command=_session.Command("SELECT fetch_id FROM market_activity_observation WHERE trade_date BETWEEN $from AND $to ORDER BY fetch_id",("$from",from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$to",through.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))))
        using(var reader=command.ExecuteReader())while(reader.Read()){ids.Add(reader.GetString(0));_session.CheckReadLimit(ids.Count);}
        var result=new List<MarketActivityKnowledgeRevision>();var healthByHash=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        foreach(var id in ids)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();var proof=ReadActivityProof(id);var registration=ReadActivityRegistration(id,proof);
            var events=ReadActivityJournal(id,registration.Value,registration.ProofHash,registration.Head);
            if(registration.Value.FirstSeenAt>cutoff)continue;
            var latest=events.Last(x=>x.At<=cutoff);
            if(!healthByHash.TryGetValue(proof.Hash,out var health)){health=_session.RawEvidence.Verify(proof.Hash);healthByHash.Add(proof.Hash,health);}
            health&=Convert.ToInt32(_session.Scalar("SELECT count(*) FROM source_fetch f WHERE f.fetch_id=$id AND f.status='Accepted' AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')",("$id",id)))==1;
            result.Add(new(id,proof.Market,proof.Date,proof.AmountYuan,proof.VolumeShares,latest.Decision,health?latest.Decision=="Rejected"?MarketCycleObservationState.Rejected:MarketCycleObservationState.Accepted:MarketCycleObservationState.Critical,
                registration.Value.FirstSeenAt,latest.At,proof.RetrievedAt,registration.Value.Origin,proof.Hash,proof.Original,proof.Url,registration.ProofHash,latest.Hash,health,latest.Command));
        }
        var conflicts=result.Where(x=>x.Decision!="Rejected").GroupBy(x=>(x.Market,x.Date)).Where(x=>x.Select(v=>(v.AmountYuan,v.VolumeShares)).Distinct().Count()>1).Select(x=>x.Key).ToHashSet();
        for(var i=0;i<result.Count;i++)if(conflicts.Contains((result[i].Market,result[i].Date))&&result[i].State==MarketCycleObservationState.Accepted)result[i]=result[i] with{State=MarketCycleObservationState.NeedsReview};
        return MarketActivityKnowledgeReport.Freeze(from,through,cutoff,result);
    }
}
