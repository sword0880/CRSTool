using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed class IndustryPeStore
{
    private readonly IMarketRepositorySession _session;
    public IndustryPeStore(IMarketRepositorySession session)=>_session=session;
    private static string IndustryTime(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    public void InitializeSchema()
    {
        var version=_session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='industry-pe'");
        if(version is not null&&Convert.ToInt32(version)!=1)throw new InvalidDataException("行业PE存储版本不受支持。");
        // 已声明初始化的表缺失属于数据损坏，不能CREATE IF NOT EXISTS后冒充从未采集的空库。
        var existingTables=Convert.ToInt32(_session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_industry_capture','nt_industry_revision','nt_industry_event','nt_industry_command')"));
        if(version is not null&&existingTables!=4||version is null&&existingTables!=0)throw new InvalidDataException("行业PE存储表与迁移记录不一致，拒绝重建为空库。");
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();
        _session.Execute("""
            CREATE TABLE IF NOT EXISTS nt_industry_capture(id TEXT PRIMARY KEY,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),trade_date TEXT NOT NULL,registered_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_industry_revision(id TEXT PRIMARY KEY,trade_date TEXT NOT NULL,sector TEXT NOT NULL,values_json TEXT NOT NULL,revision_no INTEGER NOT NULL,first_seen TEXT NOT NULL,capture_id TEXT NOT NULL REFERENCES nt_industry_capture(id),UNIQUE(trade_date,sector,revision_no));
            CREATE TABLE IF NOT EXISTS nt_industry_event(sequence INTEGER PRIMARY KEY AUTOINCREMENT,revision_id TEXT NOT NULL REFERENCES nt_industry_revision(id),state TEXT NOT NULL CHECK(state IN('Accepted','NeedsReview','Rejected','Superseded')),recorded_at TEXT NOT NULL,reviewer TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_nt_industry_date ON nt_industry_revision(trade_date,sector);
            CREATE INDEX IF NOT EXISTS ix_nt_industry_event ON nt_industry_event(revision_id,recorded_at,sequence);
            CREATE TABLE IF NOT EXISTS nt_industry_command(id TEXT PRIMARY KEY,operation TEXT NOT NULL,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,completed_at TEXT NOT NULL);
            """);
        _session.Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('industry-pe',1,$at)",("$at",IndustryTime(_session.Clock.GetUtcNow())));transaction.Commit();
    }
    /// <summary>重试先核对双回执；半份回执或操作冲突不能重新联网后覆盖原命令。</summary>
    public IndustryPeImportOutcome? IndustryPeReceipt(Guid commandId)
    {
        var value=IndustryReplay(commandId,"industry-pe-import",null);
        return value is null?null:JsonSerializer.Deserialize<IndustryPeImportOutcome>(value)??throw new InvalidDataException("行业PE回执损坏。");
    }
    public IndustryPeImportOutcome ImportIndustryPe(CapturedIndustryPeDocument document,Guid? commandId=null)
    {
        _session.OperationCancellation.ThrowIfCancellationRequested();document=document with{Content=document.Content.ToArray()};
        var values=OfficialIndustryPeSource.Parse(document,_session.Clock.GetUtcNow());
        var payload=ExitAlertEvaluator.Hash(document.Url+"|"+document.Sha256+"|"+IndustryTime(document.RetrievedAt));
        if(IndustryReplay(commandId,"industry-pe-import",payload) is {} replay)return JsonSerializer.Deserialize<IndustryPeImportOutcome>(replay)??throw new InvalidDataException("行业PE回执损坏。");
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();var now=IndustryWriteAt();
        // 原件可先落地而事务失败，最多留下未引用原件；取消或SQL失败不能留下半天的行业事实。
        var hash=_session.RawEvidence.Save(document.Content);var capture=ExitAlertEvaluator.Hash(payload);var fetch=Guid.NewGuid().ToString("N");
        var existing=IndustryPeRevisions(document.Date,document.Date,now);
        if(existing.Any(x=>(x.State is "Accepted" or "NeedsReview")&&!x.EvidenceHealthy))throw new InvalidDataException("已有行业PE原件不健康，不能用重复导入掩盖。");
        if(_session.Scalar("SELECT registered_at FROM nt_industry_capture WHERE id=$id",("$id",capture)) is string registered)
        {
            var duplicate=new IndustryPeImportOutcome(capture,0,values.Count,existing.Count(x=>x.State=="NeedsReview"),DateTimeOffset.Parse(registered,CultureInfo.InvariantCulture));
            RecordIndustry_session.Command(commandId,"industry-pe-import",payload,JsonSerializer.Serialize(duplicate),now);_session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();return duplicate;
        }
        _session.Execute("INSERT INTO source_fetch VALUES($id,'OfficialCnindexIndustryPe',$retrieved,NULL,$url,$hash,$hash,'Accepted',$parser)",("$id",fetch),("$retrieved",IndustryTime(document.RetrievedAt)),("$url",document.Url),("$hash",hash),("$parser",OfficialIndustryPeSource.ParserVersion));
        _session.Execute("INSERT INTO nt_industry_capture VALUES($id,$fetch,$date,$at)",("$id",capture),("$fetch",fetch),("$date",Day(document.Date)),("$at",IndustryTime(now)));
        var added=0;var duplicates=0;var pending=0;
        foreach(var value in values)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();var prior=existing.Where(x=>x.Values.Code==value.Code).ToArray();
            if(prior.Any(x=>x.Values==value)){duplicates++;continue;}
            var id=Guid.NewGuid().ToString("N");var state=prior.Length==0?"Accepted":"NeedsReview";
            _session.Execute("INSERT INTO nt_industry_revision VALUES($id,$date,$sector,$json,$revision,$at,$capture)",("$id",id),("$date",Day(document.Date)),("$sector",value.Code),("$json",JsonSerializer.Serialize(value)),("$revision",prior.Length==0?1:prior.Max(x=>x.Revision)+1),("$at",IndustryTime(now)),("$capture",capture));
            IndustryEvent(id,state,"official-parser:"+OfficialIndustryPeSource.ParserVersion,now);added++;if(state=="NeedsReview")pending++;
        }
        var outcome=new IndustryPeImportOutcome(capture,added,duplicates,pending,now);
        RecordIndustry_session.Command(commandId,"industry-pe-import",payload,JsonSerializer.Serialize(outcome),now);
        _session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();return outcome;
    }
    public IReadOnlyList<IndustryPeRevision> IndustryPeRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>_session.Clock.GetUtcNow())throw new InvalidDataException("行业PE查询范围或知识时点无效。");
        var rows=new List<IndustryPeRevision>();
        using(var command=_session.Command("""
            SELECT r.id,r.trade_date,r.values_json,r.revision_no,e.state,r.first_seen,e.recorded_at,r.capture_id
            FROM nt_industry_revision r JOIN nt_industry_event e ON e.sequence=(SELECT max(sequence) FROM nt_industry_event x WHERE x.revision_id=r.id AND x.recorded_at<=$at)
            WHERE r.trade_date>=$from AND r.trade_date<=$through AND r.first_seen<=$at ORDER BY r.trade_date,r.sector,r.revision_no
            """,("$at",IndustryTime(cutoff)),("$from",Day(from)),("$through",Day(through))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            _session.CheckReadLimit(rows.Count+1);
            rows.Add(new(reader.GetString(0),DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),JsonSerializer.Deserialize<IndustryPeValues>(reader.GetString(2))??throw new InvalidDataException("行业PE数值记录损坏。"),reader.GetInt32(3),reader.GetString(4),DateTimeOffset.Parse(reader.GetString(5),CultureInfo.InvariantCulture),DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture),reader.GetString(7),false));
        }
        // 同一只读快照批量取得所需来源身份，并在本次操作内去重核验原件；不逐日启动并行任务，也不跨查询缓存健康结果。
        var wanted=rows.Select(x=>x.CaptureId).ToHashSet(StringComparer.Ordinal);
        var captures=new Dictionary<string,(string Hash,string Original,bool Valid)>(StringComparer.Ordinal);
        using(var command=_session.Command("""
            SELECT c.id,f.sha256,f.original_sha256,f.source_url,c.trade_date,
            f.status='Accepted' AND f.provider='OfficialCnindexIndustryPe' AND f.parser_version=$parser AND f.retrieved_utc<=c.registered_at
            AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
            FROM nt_industry_capture c JOIN source_fetch f ON f.fetch_id=c.fetch_id WHERE c.trade_date>=$from AND c.trade_date<=$through
            """,("$parser",OfficialIndustryPeSource.ParserVersion),("$from",Day(from)),("$through",Day(through))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();
            var id=reader.GetString(0);if(!wanted.Contains(id))continue;_session.CheckReadLimit(captures.Count+1);
            var expected=OfficialIndustryPeSource.Source(DateOnly.ParseExact(reader.GetString(4),"yyyy-MM-dd",CultureInfo.InvariantCulture)).AbsoluteUri;
            captures.Add(id,(reader.GetString(1),reader.GetString(2),reader.GetInt32(5)==1&&reader.GetString(3)==expected));
        }
        var health=_session.RawEvidence.VerifyMany(captures.Values.SelectMany(x=>new[]{x.Hash,x.Original}),_session.OperationCancellation);
        return rows.Select(x=>x with{EvidenceHealthy=captures.TryGetValue(x.CaptureId,out var evidence)&&evidence.Valid&&health[evidence.Hash]&&health[evidence.Original]}).ToArray();
    }
    /// <summary>同一事务内替换旧接受版本并追加新状态；知识截止前仍能读到当时版本，不能重写历史事件。</summary>
    public void ReviewIndustryPe(string revision,bool accept,string reviewer,Guid? commandId=null)
    {
        if(string.IsNullOrWhiteSpace(reviewer)||reviewer.Length>120||reviewer.Any(char.IsControl))throw new InvalidDataException("行业PE审核人无效。");
        var payload=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{revision,accept,reviewer}));if(IndustryReplay(commandId,"industry-pe-review",payload) is not null)return;
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();var now=IndustryWriteAt();
        using var identity=_session.Command("SELECT trade_date FROM nt_industry_revision WHERE id=$id",("$id",revision));var day=identity.Execute_session.Scalar() as string??throw new InvalidDataException("行业PE修订不存在。");
        var date=DateOnly.ParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture);var all=IndustryPeRevisions(date,date,now);var target=all.Single(x=>x.Id==revision);
        if(target.State!="NeedsReview"||!target.EvidenceHealthy)throw new InvalidDataException("行业PE目标不处于可审核状态或原件不健康。");
        if(accept)foreach(var prior in all.Where(x=>x.Values.Code==target.Values.Code&&x.State=="Accepted"))IndustryEvent(prior.Id,"Superseded",reviewer,now);
        IndustryEvent(target.Id,accept?"Accepted":"Rejected",reviewer,now);RecordIndustry_session.Command(commandId,"industry-pe-review",payload,"true",now);
        _session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private DateTimeOffset IndustryWriteAt()
    {
        var now=_session.Clock.GetUtcNow();var latest=_session.Scalar("SELECT max(at) FROM (SELECT max(registered_at) AS at FROM nt_industry_capture UNION ALL SELECT max(recorded_at) AS at FROM nt_industry_event UNION ALL SELECT max(completed_at) AS at FROM nt_industry_command UNION ALL SELECT applied_at AS at FROM feature_schema_migration WHERE feature='industry-pe')") as string;
        if(latest is not null&&now<DateTimeOffset.Parse(latest,CultureInfo.InvariantCulture))throw new InvalidDataException("系统时间倒退，停止行业PE写入以保护知识时间线。");return now;
    }
    private void IndustryEvent(string id,string state,string reviewer,DateTimeOffset at)=>_session.Execute("INSERT INTO nt_industry_event(revision_id,state,recorded_at,reviewer) VALUES($id,$state,$at,$who)",("$id",id),("$state",state),("$at",IndustryTime(at)),("$who",reviewer));
    private string? IndustryReplay(Guid? id,string operation,string? payload)
    {
        if(id is not {} value)return null;var prior=_session.Scalar("SELECT operation FROM worker_business_command WHERE request=$id",("$id",value.ToString("N"))) as string;
        using var command=_session.Command("SELECT operation,payload_hash,result_json FROM nt_industry_command WHERE id=$id",("$id",value.ToString("N")));using var reader=command.ExecuteReader();
        if(!reader.Read())return prior is null?null:throw new InvalidDataException("行业PE命令双回执缺损。");
        if(prior!=operation||reader.GetString(0)!=operation||payload is not null&&reader.GetString(1)!=payload)throw new InvalidDataException("行业PE命令不能更换操作或负载。");return reader.GetString(2);
    }
    private void RecordIndustry_session.Command(Guid? id,string operation,string payload,string result,DateTimeOffset at)
    {
        if(id is not {} value)return;_session.Execute("INSERT INTO nt_industry_command VALUES($id,$op,$payload,$json,$at)",("$id",value.ToString("N")),("$op",operation),("$payload",payload),("$json",result),("$at",IndustryTime(at)));Record_session.Command(id,operation);
    }
}
