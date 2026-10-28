using System.Globalization;
using CRS.Common.Integrity;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class MarketActivityStore
{
    private sealed record ActivityProof(string Id,string Market,DateOnly Date,decimal AmountYuan,decimal VolumeShares,
        string Url,string Hash,string Original,string Provider,string Parser,DateTimeOffset RetrievedAt);
    private sealed record ActivityRegistration(ActivityProof Proof,DateTimeOffset FirstSeenAt,string Origin,string InitialDecision);
    private sealed record ActivityJournalEvent(string Decision,DateTimeOffset At,string Actor,string Cause,string? Command,string Previous,string Hash);
    private static string At(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ActivityInstant(string text)
    {
        if(text.Length>64||!System.Text.RegularExpressions.Regex.IsMatch(text,@"(?:Z|[+-][0-9]{2}:?[0-9]{2})$",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("成交登记时刻缺少明确时区，不能猜测为本机时间。");
        return DateTimeOffset.Parse(text,CultureInfo.InvariantCulture,DateTimeStyles.None);
    }
    private static string Hash(string value)=>ContentFingerprint.Sha256(System.Text.Encoding.UTF8.GetBytes(value));
    private static string EventHash(string id,ActivityJournalEvent value)=>Hash(JsonSerializer.Serialize(new{id,value.Decision,At=At(value.At),value.Actor,value.Cause,value.Command,value.Previous}));
    private void InitializeKnowledgeSchema()
    {
        var version=_session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='market-activity-knowledge'");
        var count=Convert.ToInt32(_session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('market_activity_registration','market_activity_state_event')"));
        if(version is not null&&(Convert.ToInt32(version)!=1||count!=2)||version is null&&count!=0)
            throw new InvalidDataException("成交知识时间链迁移与结构不一致，拒绝重建为空资料。");
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();
        _session.Execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS ix_market_activity_fetch ON market_activity_observation(fetch_id);
            CREATE TABLE IF NOT EXISTS market_activity_registration(fetch_id TEXT PRIMARY KEY REFERENCES source_fetch(fetch_id),registered_at TEXT NOT NULL,origin TEXT NOT NULL,proof_json TEXT NOT NULL,proof_hash TEXT NOT NULL,head_hash TEXT);
            CREATE TABLE IF NOT EXISTS market_activity_state_event(sequence INTEGER PRIMARY KEY AUTOINCREMENT,fetch_id TEXT NOT NULL REFERENCES market_activity_registration(fetch_id),decision TEXT NOT NULL CHECK(decision IN('Observed','Accepted','Rejected')),recorded_at TEXT NOT NULL,actor TEXT NOT NULL,cause TEXT NOT NULL,command_id TEXT,previous_hash TEXT NOT NULL,event_hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_market_activity_state ON market_activity_state_event(fetch_id,sequence);
            """);
        if(version is null)
        {
            // 旧版缺少首次登记及竞争版本否决历史；只从实际迁移时刻起可知，不能回填过去已经可见。
            var ids=new List<string>();using(var command=_session.Command("SELECT fetch_id FROM market_activity_observation ORDER BY fetch_id"))using(var reader=command.ExecuteReader())while(reader.Read())ids.Add(reader.GetString(0));
            var now=ActivityWriteAt();foreach(var id in ids){_session.OperationCancellation.ThrowIfCancellationRequested();RegisterActivityKnowledge(id,"LegacyObservedNow",now);}
            _session.Execute("INSERT INTO feature_schema_migration VALUES('market-activity-knowledge',1,$now)",("$now",At(now)));
        }
        if(Convert.ToInt32(_session.Scalar("SELECT count(*) FROM market_activity_observation o LEFT JOIN market_activity_registration r USING(fetch_id) WHERE r.fetch_id IS NULL"))!=0
            ||Convert.ToInt32(_session.Scalar("SELECT count(*) FROM market_activity_registration r LEFT JOIN market_activity_observation o USING(fetch_id) WHERE o.fetch_id IS NULL"))!=0)
            throw new InvalidDataException("成交事实和知识登记不完整，拒绝自动补写历史。");
        transaction.Commit();
    }
    private DateTimeOffset ActivityWriteAt()
    {
        var now=_session.Clock.GetUtcNow();
        var latest=_session.Scalar("SELECT at FROM (SELECT registered_at AS at FROM market_activity_registration UNION ALL SELECT recorded_at AS at FROM market_activity_state_event) ORDER BY julianday(at) DESC LIMIT 1") as string;
        if(latest is not null&&ActivityInstant(latest)>now)throw new InvalidDataException("成交知识时钟倒退，拒绝改写登记及审核时间。");return now;
    }
    private ActivityProof ReadActivityProof(string id)
    {
        using var command=_session.Command("SELECT o.market,o.trade_date,o.amount_yuan,o.volume_shares,f.source_url,f.sha256,f.original_sha256,f.provider,f.parser_version,f.retrieved_utc FROM market_activity_observation o LEFT JOIN source_fetch f USING(fetch_id) WHERE o.fetch_id=$id",("$id",id));using var reader=command.ExecuteReader();
        if(!reader.Read()||Enumerable.Range(0,10).Any(reader.IsDBNull))throw new InvalidDataException("成交原件或金融事实缺损。");
        var proof=new ActivityProof(id,reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),ActivityInstant(reader.GetString(9)));
        if(proof.Market is not ("SSE" or "SZSE")||proof.AmountYuan<0||proof.VolumeShares<0||proof.Hash.Length!=64||!proof.Hash.All(Uri.IsHexDigit)||proof.Hash!=proof.Original||proof.Provider!="OfficialMarketActivity"||proof.Parser!="market-activity-v1")throw new InvalidDataException("成交登记的市场、数值或原件身份无效。");return proof;
    }
    private void RegisterActivityKnowledge(string id,string origin,DateTimeOffset at)
    {
        var proof=ReadActivityProof(id);
        if(proof.RetrievedAt==default||proof.RetrievedAt>at||proof.Date>DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("成交原件日期或取得时间超过登记时点。");
        var decision=_session.Scalar("SELECT decision FROM market_activity_review WHERE fetch_id=$id",("$id",id)) as string??"Observed";
        if(decision is not ("Observed" or "Accepted" or "Rejected"))throw new InvalidDataException("旧版成交审核基线无效。");
        var payload=JsonSerializer.Serialize(new ActivityRegistration(proof,at.ToUniversalTime(),origin,decision));
        _session.Execute("INSERT INTO market_activity_registration VALUES($id,$at,$origin,$proof,$hash,NULL)",("$id",id),("$at",At(at)),("$origin",origin),("$proof",payload),("$hash",Hash(payload)));
        AddActivityEvent(id,decision,at,origin=="Native"?"OfficialAdapter":"SystemMigration",origin=="Native"?"SourceObserved":"LegacyBaselineAtMigration",null);
    }
    private void AddActivityEvent(string id,string decision,DateTimeOffset at,string actor,string cause,Guid? commandId)
    {
        var head=_session.Scalar("SELECT head_hash FROM market_activity_registration WHERE fetch_id=$id",("$id",id)) as string;
        var last=_session.Scalar("SELECT event_hash FROM market_activity_state_event WHERE fetch_id=$id ORDER BY sequence DESC LIMIT 1",("$id",id)) as string;
        if(head!=last)throw new InvalidDataException("成交审核事件链头不完整，拒绝追加掩盖。");
        var previous=head??_session.Scalar("SELECT proof_hash FROM market_activity_registration WHERE fetch_id=$id",("$id",id)) as string??throw new InvalidDataException("成交首次登记缺失。");
        var value=new ActivityJournalEvent(decision,at,actor,cause,commandId?.ToString("N"),previous,"");var hash=EventHash(id,value);
        _session.Execute("INSERT INTO market_activity_state_event(fetch_id,decision,recorded_at,actor,cause,command_id,previous_hash,event_hash) VALUES($id,$d,$at,$actor,$cause,$command,$previous,$hash)",("$id",id),("$d",decision),("$at",At(at)),("$actor",actor),("$cause",cause),("$command",value.Command),("$previous",previous),("$hash",hash));
        _session.Execute("UPDATE market_activity_registration SET head_hash=$hash WHERE fetch_id=$id",("$hash",hash),("$id",id));
    }
    private void RecordActivityDecision(string id,string decision,string reviewer,DateTimeOffset at,string cause,Guid? commandId)
    {
        _session.Execute("INSERT INTO market_activity_review VALUES($id,$s,$who,$now) ON CONFLICT(fetch_id) DO UPDATE SET decision=excluded.decision,reviewer=excluded.reviewer,reviewed_at=excluded.reviewed_at",("$id",id),("$s",decision),("$who",reviewer),("$now",At(at)));
        _session.Execute("INSERT INTO market_activity_review_audit VALUES($event,$id,$s,$who,$now)",("$event",Guid.NewGuid().ToString("N")),("$id",id),("$s",decision),("$who",reviewer),("$now",At(at)));
        AddActivityEvent(id,decision,at,reviewer,cause,commandId);
    }
}
