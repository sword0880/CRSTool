using System.Globalization;
using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>广度人工档案独立存储，记录真实核验时刻；缺资料与冲突都不能退化为零。</summary>
internal sealed partial class MarketBreadthStore(IMarketRepositorySession session)
{
    private const string Definition="SSE-SZSE-Ashares-point-in-time-v1";
    private sealed record Proof(MarketBreadthArchive Archive,string OriginalHash,string NormalizedHash,string Url,
        DateTimeOffset RetrievedAt,DateTimeOffset RegisteredAt,string Reviewer,string Reason,string FetchId);
    public void InitializeSchema()
    {
        var version=session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='market-breadth'");
        var count=Convert.ToInt32(session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_breadth_archive','nt_breadth_command')"));
        if(version is null&&count!=0||version is not null&&(Convert.ToInt32(version)!=1||count!=2))throw new InvalidDataException("市场广度结构不完整，拒绝重建为空资料。");
        session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();
        session.Execute("""
            CREATE TABLE IF NOT EXISTS nt_breadth_archive(id TEXT PRIMARY KEY,day TEXT NOT NULL,proof_json TEXT NOT NULL,registered_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_breadth_command(id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,archive_id TEXT NOT NULL REFERENCES nt_breadth_archive(id));
            CREATE INDEX IF NOT EXISTS ix_nt_breadth_day ON nt_breadth_archive(day);
            """);
        session.Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('market-breadth',1,$at)",("$at",Time(session.Clock.GetUtcNow())));transaction.Commit();InitializeReviewSchema();
    }
    public string Import(byte[] original,byte[] normalized,Uri source,DateTimeOffset retrievedAt,string reviewer,string reason,bool confirmed,Guid commandId,bool reviewed=true)
    {
        original=original.ToArray();normalized=normalized.ToArray();
        if(!confirmed||commandId==Guid.Empty||original.Length is <1 or >8388608||normalized.Length is <1 or >8388608
            ||!source.IsAbsoluteUri||source.Scheme!="https"||source.UserInfo.Length!=0||source.AbsoluteUri.Length>2000
            ||!Text(reviewer,120)||!Text(reason,1000))throw new InvalidDataException("广度档案需要有界原件、HTTPS来源、明确人工确认及核验说明。");
        var archive=Parse(normalized);var now=session.Clock.GetUtcNow();
        if(retrievedAt==default||retrievedAt>now||archive.Date>DateOnly.FromDateTime(retrievedAt.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("广度资料日期或取得时刻不成立。");
        var originalHash=ContentFingerprint.Sha256(original);var normalizedHash=ContentFingerprint.Sha256(normalized);
        var payload=ImportPayload(originalHash,normalizedHash,source.AbsoluteUri,retrievedAt,reviewer,reason,reviewed);
        using(var command=session.Command("SELECT payload_hash,archive_id FROM nt_breadth_command WHERE id=$id",("$id",commandId.ToString("D"))))
        using(var reader=command.ExecuteReader())if(reader.Read())
        {if(reader.GetString(0)!=payload)throw new InvalidDataException("广度命令标识已绑定其他资料。");return reader.GetString(1);}
        session.OperationCancellation.ThrowIfCancellationRequested();session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();
        var latest=session.Scalar("SELECT max(registered_at) FROM nt_breadth_archive") as string;
        if(latest is not null&&DateTimeOffset.Parse(latest,CultureInfo.InvariantCulture)>now)throw new InvalidDataException("广度核验时钟倒退。");
        CheckReviewClock(now);
        if(session.Scalar("SELECT archive_id FROM nt_breadth_command WHERE payload_hash=$hash LIMIT 1",("$hash",payload)) is string existing)
        {
            session.Execute("INSERT INTO nt_breadth_command VALUES($command,$payload,$id)",("$command",commandId.ToString("D")),("$payload",payload),("$id",existing));
            session.RecordCommand(commandId,reviewed?"market-breadth-reviewed-import":"market-breadth-submit");transaction.Commit();return existing;
        }
        session.RawEvidence.Save(original);session.RawEvidence.Save(normalized);
        var fetch=Guid.NewGuid().ToString("N");
        session.Execute("INSERT INTO source_fetch VALUES($id,'ReviewedMarketBreadthArchive',$at,NULL,$url,$normalized,$original,'Accepted','reviewed-breadth-v1')",("$id",fetch),("$at",Time(retrievedAt)),("$url",source.AbsoluteUri),("$normalized",normalizedHash),("$original",originalHash));
        var proof=new Proof(archive,originalHash,normalizedHash,source.AbsoluteUri,retrievedAt.ToUniversalTime(),now.ToUniversalTime(),reviewer,reason,fetch);
        var json=JsonSerializer.Serialize(proof);var id=Hash(json);
        session.Execute("INSERT OR IGNORE INTO nt_breadth_archive VALUES($id,$day,$proof,$at)",("$id",id),("$day",archive.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$proof",json),("$at",Time(now)));
        AppendState(id,reviewed?"Accepted":"NeedsReview",now,reviewer,reason,commandId.ToString("N"));
        session.Execute("INSERT INTO nt_breadth_command VALUES($command,$payload,$id)",("$command",commandId.ToString("D")),("$payload",payload),("$id",id));
        session.RecordCommand(commandId,reviewed?"market-breadth-reviewed-import":"market-breadth-submit");session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();return id;
    }
    public (IReadOnlyList<MarketCycleBreadthPoint> Points,IReadOnlyList<MarketCycleSourceReference> Sources) Read(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var rows=Revisions(from,through,cutoff).Where(x=>x.Decision is not ("Rejected" or "Superseded")).ToArray();
        return(rows.Select(x=>new MarketCycleBreadthPoint(x.Date,x.AboveMa20Count,x.AboveMa60Count,x.NewLowCount,x.UniverseCount,x.UniverseSnapshotId,x.UniverseHash,x.DefinitionVersion,null,x.StateRecordedAt,x.RetrievedAt,x.Id,x.NormalizedHash,x.State)).ToArray(),
            rows.Select(x=>new MarketCycleSourceReference("Breadth",x.Date,x.Id,"ReviewedNormalizedArchive",x.SourceUrl,x.NormalizedHash,x.OriginalHash,x.RetrievedAt,x.RegisteredAt,x.StateRecordedAt,x.State.ToString())).ToArray());
    }
    private IReadOnlyList<MarketBreadthRevision> Revisions(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var rows=new List<(string Id,Proof Proof)>();
        using(var command=session.Command("SELECT id,day,proof_json,registered_at FROM nt_breadth_archive WHERE day>=$from AND day<=$through ORDER BY day,id",("$from",from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),("$through",through.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            session.OperationCancellation.ThrowIfCancellationRequested();session.CheckReadLimit(rows.Count+1);
            var json=reader.GetString(2);var proof=JsonSerializer.Deserialize<Proof>(json)??throw new InvalidDataException("广度证明损坏。");
            if(Hash(json)!=reader.GetString(0)||proof.Archive is null||proof.Archive.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)!=reader.GetString(1)
                ||Time(proof.RegisteredAt)!=reader.GetString(3)||proof.RetrievedAt>proof.RegisteredAt||!Text(proof.Reviewer,120)||!Text(proof.Reason,1000))throw new InvalidDataException("广度证明身份或登记时刻不符。");
            if(proof.RegisteredAt<=cutoff)rows.Add((reader.GetString(0),proof));
        }
        var health=session.RawEvidence.VerifyMany(rows.SelectMany(x=>new[]{x.Proof.OriginalHash,x.Proof.NormalizedHash}),session.OperationCancellation);
        var result=new List<MarketBreadthRevision>();
        foreach(var (id,p) in rows)
        {
            var a=p.Archive;Validate(a);
            var healthy=health.GetValueOrDefault(p.OriginalHash)&&health.GetValueOrDefault(p.NormalizedHash)
                &&Convert.ToInt32(session.Scalar("SELECT count(*) FROM source_fetch f WHERE fetch_id=$id AND provider='ReviewedMarketBreadthArchive' AND parser_version='reviewed-breadth-v1' AND status='Accepted' AND sha256=$n AND original_sha256=$o AND source_url=$url AND retrieved_utc=$at AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')",("$id",p.FetchId),("$n",p.NormalizedHash),("$o",p.OriginalHash),("$url",p.Url),("$at",Time(p.RetrievedAt))))==1;
            if(healthy&&JsonSerializer.Serialize(Parse(session.RawEvidence.ReadVerified(p.NormalizedHash,session.OperationCancellation)))!=JsonSerializer.Serialize(a))throw new InvalidDataException("广度金融值与保存的标准化档案不符。");
            var stateEvent=ReadState(id,p.RegisteredAt,cutoff);
            var state=stateEvent.State is "Rejected" or "Superseded"?MarketCycleObservationState.Rejected:!healthy?MarketCycleObservationState.Critical:stateEvent.State=="Accepted"?MarketCycleObservationState.Accepted:MarketCycleObservationState.NeedsReview;
            // 同日出现另一份核验档案时保留竞争事实；评估器将重复日期作为冲突关闭信号。
            var universeHash=ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(new{a.UniverseDate,a.UniverseDefinitionVersion,Members=a.Members.Order(StringComparer.Ordinal).ToArray()}));
            result.Add(new(id,a.Date,a.UniverseSnapshotId,a.Members.Count,a.Observations.Count(x=>x.Close>x.Ma20),a.Observations.Count(x=>x.Close>x.Ma60),a.Observations.Count(x=>x.Close<=x.Prior250Low),
                universeHash,a.UniverseDefinitionVersion,p.OriginalHash,p.NormalizedHash,p.Url,p.RetrievedAt,p.RegisteredAt,stateEvent.At,stateEvent.State,state,healthy,stateEvent.Reviewer,stateEvent.Reason,stateEvent.Hash,stateEvent.Command));
        }
        return result;
    }
    private static MarketBreadthArchive Parse(byte[] bytes)
    {
        try{var value=CRS.Serialization.StrictJson.Read<MarketBreadthArchive>(System.Text.Encoding.UTF8.GetString(bytes),8388608);Validate(value);return value;}
        catch(JsonException error){throw new InvalidDataException("广度档案格式无效。",error);}
    }
    private static void Validate(MarketBreadthArchive a)
    {
        if(a.Version!="reviewed-breadth-v1"||a.Date==default||a.UniverseDate!=a.Date||a.UniverseDefinitionVersion!=Definition||!Text(a.UniverseSnapshotId,150)
            ||a.PriceBasis!="BackAdjustedToObservationDate"||a.Members is null||a.Observations is null||a.Members.Count is <1 or >20000||a.Members.Count!=a.Observations.Count
            ||a.Members.Any(x=>x is null||!(x.Length==10&&x.StartsWith("SSE:",StringComparison.Ordinal)&&x[4..].All(char.IsAsciiDigit)||x.Length==11&&x.StartsWith("SZSE:",StringComparison.Ordinal)&&x[5..].All(char.IsAsciiDigit)))
            ||a.Observations.Any(x=>x is null)
            ||a.Members.Distinct(StringComparer.Ordinal).Count()!=a.Members.Count||!a.Members.Order(StringComparer.Ordinal).SequenceEqual(a.Observations.Select(x=>x.Security).Order(StringComparer.Ordinal))
            ||a.Observations.Any(x=>x.Close<=0||x.Ma20<=0||x.Ma60<=0||x.Prior250Low<=0))throw new InvalidDataException("广度证券宇宙、逐证券值或复权口径不完整。");
    }
    private static bool Text(string? text,int max)=>!string.IsNullOrWhiteSpace(text)&&text.Length<=max&&!text.Any(char.IsControl);
    private static string Hash(string text)=>ContentFingerprint.Sha256(System.Text.Encoding.UTF8.GetBytes(text));
    private static string Time(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}
