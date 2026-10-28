using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class IndexSeriesStore
{
    private readonly IMarketRepositorySession _session;
    public IndexSeriesStore(IMarketRepositorySession session)=>_session=session;
    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static string IndustryTime(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private sealed record IndexDocumentIdentity(string Role,string Url,string Hash,DateTimeOffset RetrievedAt);
    private sealed record IndexCaptureProof(string Kind,string? Code,DateOnly From,DateOnly Through,IReadOnlyList<IndexDocumentIdentity> Documents,IReadOnlyDictionary<string,string> PriceReferences,IReadOnlyList<IndexSeriesValue> Values);
    public void InitializeSchema()
    {
        var version=_session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='index-series'");
        var tables=Convert.ToInt32(_session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_index_capture','nt_index_capture_fetch','nt_index_revision','nt_index_event','nt_index_command','nt_index_price_reference')"));
        if(version is not null&&(Convert.ToInt32(version)!=1||tables!=6)||version is null&&tables!=0)throw new InvalidDataException("指数事实链迁移与表结构不一致，拒绝重建为空库。");
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();_session.Execute("""
            CREATE TABLE IF NOT EXISTS nt_index_capture(id TEXT PRIMARY KEY,kind TEXT NOT NULL,code TEXT,from_day TEXT NOT NULL,through_day TEXT NOT NULL,method_hash TEXT NOT NULL,proof_json TEXT NOT NULL,registered_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_index_capture_fetch(capture_id TEXT NOT NULL REFERENCES nt_index_capture(id),role TEXT NOT NULL,fetch_id TEXT NOT NULL REFERENCES source_fetch(fetch_id),PRIMARY KEY(capture_id,role));
            CREATE TABLE IF NOT EXISTS nt_index_revision(id TEXT PRIMARY KEY,code TEXT NOT NULL,day TEXT NOT NULL,kind TEXT NOT NULL,values_json TEXT NOT NULL,revision_no INTEGER NOT NULL,first_seen TEXT NOT NULL,capture_id TEXT NOT NULL REFERENCES nt_index_capture(id),UNIQUE(code,day,kind,revision_no));
            CREATE TABLE IF NOT EXISTS nt_index_event(sequence INTEGER PRIMARY KEY AUTOINCREMENT,revision_id TEXT NOT NULL REFERENCES nt_index_revision(id),state TEXT NOT NULL CHECK(state IN('Accepted','NeedsReview','Rejected','Superseded')),recorded_at TEXT NOT NULL,reviewer TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_index_command(id TEXT PRIMARY KEY,operation TEXT NOT NULL,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,completed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS nt_index_price_reference(capture_id TEXT NOT NULL REFERENCES nt_index_capture(id),code TEXT NOT NULL,revision_id TEXT NOT NULL REFERENCES nt_index_revision(id),PRIMARY KEY(capture_id,code));
            CREATE INDEX IF NOT EXISTS ix_nt_index_day ON nt_index_revision(day,code,kind);
            CREATE INDEX IF NOT EXISTS ix_nt_index_event ON nt_index_event(revision_id,recorded_at,sequence);
            """);
        _session.Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('index-series',1,$at)",("$at",IndustryTime(_session.Clock.GetUtcNow())));transaction.Commit();
    }
    public IndexImportOutcome? IndexSeriesReceipt(Guid commandId)
    {
        var value=IndexReplay(commandId,"index-series-import",null);if(value is null)return null;
        var result=JsonSerializer.Deserialize<IndexImportOutcome>(value)??throw new InvalidDataException("指数命令回执损坏。");
        if(result.Kind is not ("Price" or "Valuation")||_session.Scalar("SELECT count(*) FROM nt_index_capture WHERE id=$id AND kind=$kind AND code IS $code AND from_day=$from AND through_day=$through",("$id",result.CaptureId),("$kind",result.Kind),("$code",result.Code),("$from",Day(result.From)),("$through",Day(result.Through))) is not {} found||Convert.ToInt32(found)!=1)throw new InvalidDataException("指数回执种类或区间与捕获身份不一致。");return result;
    }
    public IndexImportOutcome ImportIndexSeries(CapturedIndexPacket packet,Guid? commandId=null)
    {
        // 冻结每份原件后再解析和散列；外部持有byte[]不能在核验与持久化之间换内容。
        packet=packet with{Documents=packet.Documents.Select(x=>x with{Content=x.Content.ToArray()}).ToArray()};
        _session.OperationCancellation.ThrowIfCancellationRequested();var values=OfficialIndexSeriesSource.Parse(packet,_session.Clock.GetUtcNow());
        if(values.Count==0)throw new InvalidDataException("指数原件未包含任何日事实，不能以空导入确认覆盖完成。");
        var identities=packet.Documents.OrderBy(x=>x.Role,StringComparer.Ordinal).Select(x=>new IndexDocumentIdentity(x.Role,x.Url,x.Sha256,x.RetrievedAt.ToUniversalTime())).ToArray();
        var payload=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{packet.Kind,packet.Code,packet.From,packet.Through,Documents=identities}));
        if(IndexReplay(commandId,"index-series-import",payload) is {} previous)return JsonSerializer.Deserialize<IndexImportOutcome>(previous)??throw new InvalidDataException("指数命令回执损坏。");
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();var now=IndexWriteAt();
        if(identities.Any(x=>x.RetrievedAt>now))throw new InvalidDataException("原件取得时间超过指数登记时刻。");
        var existing=ReadIndexSeries(packet.From,packet.Through,now,validatePriceReferences:false);
        var relevant=existing.Where(x=>x.Value.Kind==packet.Kind&&values.Any(v=>v.Code==x.Value.Code)).ToArray();
        if(relevant.Any(x=>(x.State is "Accepted" or "NeedsReview")&&!x.EvidenceHealthy))throw new InvalidDataException("已有指数原件或方法证据不健康，不能重复导入掩盖。");
        var references=new SortedDictionary<string,string>(StringComparer.Ordinal);
        if(packet.Kind=="Valuation")foreach(var value in values)
        {
            var day=existing.Where(x=>x.Value.Kind=="Price"&&x.Value.Code==value.Code&&x.Value.Date==value.Date).ToArray();var accepted=day.Where(x=>x.State=="Accepted"&&x.EvidenceHealthy).ToArray();
            if(day.Any(x=>x.State=="NeedsReview")||accepted.Length!=1||accepted[0].Value.Close!=value.Close)throw new InvalidDataException("估值统计日缺少唯一健康且收盘一致的指数价格；不能凭全局日期头回填。");
            references.Add(value.Code,accepted[0].Id);
        }
        // 摘要同时覆盖解析金融事实；仅核对原件文件存在，不能发现values_json被意外改写。
        var proof=new IndexCaptureProof(packet.Kind,packet.Code,packet.From,packet.Through,identities,references,values);var proofJson=JsonSerializer.Serialize(proof);var capture=ExitAlertEvaluator.Hash(proofJson);
        var registered=_session.Scalar("SELECT registered_at FROM nt_index_capture WHERE id=$id",("$id",capture)) as string;
        if(registered is null)
        {
            _session.Execute("INSERT INTO nt_index_capture VALUES($id,$kind,$code,$from,$through,$method,$proof,$at)",("$id",capture),("$kind",packet.Kind),("$code",packet.Code),("$from",Day(packet.From)),("$through",Day(packet.Through)),("$method",identities.Single(x=>x.Role=="Method").Hash),("$proof",proofJson),("$at",IndustryTime(now)));
            foreach(var document in packet.Documents)
            {
                _session.OperationCancellation.ThrowIfCancellationRequested();var hash=_session.RawEvidence.Save(document.Content);
                // 同次HTTP取得的方法/单位证明可关联多个指数，不把共享原件复制登记成多次下载。
                var fetch=_session.Scalar("SELECT fetch_id FROM source_fetch WHERE provider='OfficialCnindexIndexSeries' AND parser_version=$parser AND source_url=$url AND sha256=$h AND original_sha256=$h AND retrieved_utc=$at AND status='Accepted' LIMIT 1",("$parser",OfficialIndexSeriesSource.ParserVersion),("$url",document.Url),("$h",hash),("$at",IndustryTime(document.RetrievedAt))) as string;
                if(fetch is null)
                {
                    fetch=Guid.NewGuid().ToString("N");
                    _session.Execute("INSERT INTO source_fetch VALUES($id,'OfficialCnindexIndexSeries',$at,NULL,$url,$h,$h,'Accepted',$parser)",("$id",fetch),("$at",IndustryTime(document.RetrievedAt)),("$url",document.Url),("$h",hash),("$parser",OfficialIndexSeriesSource.ParserVersion));
                }
                _session.Execute("INSERT INTO nt_index_capture_fetch VALUES($capture,$role,$fetch)",("$capture",capture),("$role",document.Role),("$fetch",fetch));
            }
            foreach(var pair in references)_session.Execute("INSERT INTO nt_index_price_reference VALUES($id,$code,$revision)",("$id",capture),("$code",pair.Key),("$revision",pair.Value));
        }
        var added=0;var duplicates=0;var pending=0;
        foreach(var value in values)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();var prior=relevant.Where(x=>x.Value.Code==value.Code&&x.Value.Date==value.Date).ToArray();
            // 被拒绝/替代的旧值不能吞掉新候选；估值数值相同但价格基线已变也必须重新核对证明。
            if(prior.Any(x=>(x.State is "Accepted" or "NeedsReview")&&x.Value==value&&(packet.Kind=="Price"||_session.Scalar("SELECT revision_id FROM nt_index_price_reference WHERE capture_id=$id AND code=$code",("$id",x.CaptureId),("$code",value.Code)) as string==references[value.Code]))){duplicates++;continue;}
            var id=Guid.NewGuid().ToString("N");var state=prior.Length==0?"Accepted":"NeedsReview";
            _session.Execute("INSERT INTO nt_index_revision VALUES($id,$code,$day,$kind,$json,$revision,$at,$capture)",("$id",id),("$code",value.Code),("$day",Day(value.Date)),("$kind",value.Kind),("$json",JsonSerializer.Serialize(value)),("$revision",prior.Length==0?1:prior.Max(x=>x.Revision)+1),("$at",IndustryTime(now)),("$capture",capture));
            IndexEvent(id,state,"official-parser:"+OfficialIndexSeriesSource.ParserVersion,now);added++;if(state=="NeedsReview")pending++;
        }
        // 这是本命令提交时刻；相同原件的新复核可以晚于原捕获，每条修订仍保留自己的首次登记时间。
        var outcome=new IndexImportOutcome(capture,added,duplicates,pending,now){Kind=packet.Kind,Code=packet.Code,From=packet.From,Through=packet.Through};
        RecordIndexCommand(commandId,"index-series-import",payload,JsonSerializer.Serialize(outcome),now);_session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();return outcome;
    }
    public IReadOnlyList<IndexSeriesRevision> IndexSeriesRevisions(DateOnly from,DateOnly through,DateTimeOffset cutoff)=>ReadIndexSeries(from,through,cutoff,true);
    private IReadOnlyList<IndexSeriesRevision> ReadIndexSeries(DateOnly from,DateOnly through,DateTimeOffset cutoff,bool validatePriceReferences)
    {
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>_session.Clock.GetUtcNow())throw new InvalidDataException("指数读取范围或知识截止无效。");
        var rows=new List<IndexSeriesRevision>();
        using(var command=_session.Command("""
            SELECT r.id,r.code,r.day,r.kind,r.values_json,r.revision_no,r.first_seen,r.capture_id,e.state,e.recorded_at,c.method_hash,c.registered_at
            FROM nt_index_revision r LEFT JOIN nt_index_capture c ON c.id=r.capture_id
            LEFT JOIN nt_index_event e ON e.sequence=(SELECT max(sequence) FROM nt_index_event x WHERE x.revision_id=r.id AND x.recorded_at<=$at)
            WHERE r.day>=$from AND r.day<=$through AND r.first_seen<=$at ORDER BY r.day,r.code,r.kind,r.revision_no
            """,("$from",Day(from)),("$through",Day(through)),("$at",IndustryTime(cutoff))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            // 缺捕获或状态记录必须失败关闭，不能通过内连接丢掉待审修订后恢复旧值。
            if(reader.IsDBNull(8)||reader.IsDBNull(10)||reader.IsDBNull(11))throw new InvalidDataException("指数修订缺少捕获或知识状态记录。");
            _session.CheckReadLimit(rows.Count+1);var value=JsonSerializer.Deserialize<IndexSeriesValue>(reader.GetString(4))??throw new InvalidDataException("指数金融事实损坏。");
            if(value.Code!=reader.GetString(1)||Day(value.Date)!=reader.GetString(2)||value.Kind!=reader.GetString(3)||value.Kind is not ("Price" or "Valuation")||value.Close<=0)throw new InvalidDataException("指数事实与数据库身份不一致。");
            _=IndexResearchCatalog.Get(value.Code);var first=DateTimeOffset.Parse(reader.GetString(6),CultureInfo.InvariantCulture);var stateAt=DateTimeOffset.Parse(reader.GetString(9),CultureInfo.InvariantCulture);
            rows.Add(new(reader.GetString(0),value,reader.GetInt32(5),reader.GetString(8),first,stateAt,reader.GetString(7),first<=stateAt&&first>=DateTimeOffset.Parse(reader.GetString(11),CultureInfo.InvariantCulture),reader.GetString(10)));
        }
        var wanted=rows.Select(x=>x.CaptureId).ToHashSet(StringComparer.Ordinal);var proofs=new Dictionary<string,IndexCaptureProof>(StringComparer.Ordinal);var valid=new Dictionary<string,bool>(StringComparer.Ordinal);
        using(var command=_session.Command("SELECT id,proof_json,registered_at,method_hash FROM nt_index_capture WHERE from_day<=$through AND through_day>=$from",("$from",Day(from)),("$through",Day(through))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var id=reader.GetString(0);if(!wanted.Contains(id))continue;_session.OperationCancellation.ThrowIfCancellationRequested();_session.CheckReadLimit(proofs.Count+1);
            try
            {
                var proof=JsonSerializer.Deserialize<IndexCaptureProof>(reader.GetString(1))??throw new JsonException();var at=DateTimeOffset.Parse(reader.GetString(2),CultureInfo.InvariantCulture);
                proofs.Add(id,proof);valid[id]=ExitAlertEvaluator.Hash(reader.GetString(1))==id&&proof.Documents.Count==(proof.Kind=="Price"?3:4)&&proof.Documents.Select(x=>x.Role).Distinct().Count()==proof.Documents.Count&&proof.Documents.All(x=>x.RetrievedAt<=at&&Uri.TryCreate(x.Url,UriKind.Absolute,out var uri)&&OfficialIndexSeriesSource.IsSource(uri))&&proof.Documents.SingleOrDefault(x=>x.Role=="Method")?.Hash==reader.GetString(3);
            }
            catch(Exception error) when(error is JsonException or FormatException or InvalidOperationException){valid[id]=false;}
        }
        var hashes=new List<string>();var links=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        using(var command=_session.Command("""
            SELECT l.capture_id,l.role,f.sha256,f.original_sha256,f.source_url,f.retrieved_utc,
            f.status='Accepted' AND f.provider='OfficialCnindexIndexSeries' AND f.parser_version=$parser
            AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
            FROM nt_index_capture_fetch l JOIN nt_index_capture c ON c.id=l.capture_id JOIN source_fetch f ON f.fetch_id=l.fetch_id
            WHERE c.from_day<=$through AND c.through_day>=$from
            """,("$from",Day(from)),("$through",Day(through)),("$parser",OfficialIndexSeriesSource.ParserVersion)))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var id=reader.GetString(0);if(!wanted.Contains(id)||!proofs.TryGetValue(id,out var proof))continue;_session.OperationCancellation.ThrowIfCancellationRequested();
            var role=reader.GetString(1);var hash=reader.GetString(2);var original=reader.GetString(3);var expected=proof.Documents.SingleOrDefault(x=>x.Role==role);
            if(!links.TryGetValue(id,out var group)){group=new(StringComparer.Ordinal);links.Add(id,group);}group.Add(role,hash);hashes.Add(hash);hashes.Add(original);
            valid[id]&=expected is not null&&expected.Hash==hash&&original==hash&&expected.Url==reader.GetString(4)&&DateTimeOffset.TryParse(reader.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.None,out var retrieved)&&expected.RetrievedAt==retrieved&&reader.GetInt32(6)==1;
        }
        var health=_session.RawEvidence.VerifyMany(hashes,_session.OperationCancellation);
        var references=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        using(var command=_session.Command("SELECT r.capture_id,r.code,r.revision_id FROM nt_index_price_reference r JOIN nt_index_capture c ON c.id=r.capture_id WHERE c.from_day<=$through AND c.through_day>=$from",("$from",Day(from)),("$through",Day(through))))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            var id=reader.GetString(0);if(!wanted.Contains(id))continue;if(!references.TryGetValue(id,out var group)){group=new(StringComparer.Ordinal);references.Add(id,group);}group.Add(reader.GetString(1),reader.GetString(2));
        }
        foreach(var id in wanted)
        {
            var refs=references.GetValueOrDefault(id)??new Dictionary<string,string>();
            valid[id]=valid.GetValueOrDefault(id)&&proofs.TryGetValue(id,out var proof)&&links.TryGetValue(id,out var group)&&group.Count==proof.Documents.Count&&group.Values.All(x=>health.TryGetValue(x,out var okay)&&okay)
                &&proof.PriceReferences.Count==refs.Count&&proof.PriceReferences.All(x=>refs.TryGetValue(x.Key,out var found)&&found==x.Value);
        }
        var result=rows.Select(x=>x with{EvidenceHealthy=x.EvidenceHealthy&&valid.GetValueOrDefault(x.CaptureId)&&proofs.TryGetValue(x.CaptureId,out var proof)&&proof.Values is not null&&proof.Values.Count(v=>v.Code==x.Value.Code&&v.Date==x.Value.Date&&v.Kind==x.Value.Kind)==1&&proof.Values.Any(v=>v==x.Value)&&x.MethodHash==IndexResearchCatalog.Get(x.Value.Code).MethodHash}).ToArray();
        if(validatePriceReferences)for(var i=0;i<result.Length;i++)if(result[i].Value.Kind=="Valuation")
        {
            var row=result[i];var prices=result.Where(x=>x.Value.Code==row.Value.Code&&x.Value.Date==row.Value.Date&&x.Value.Kind=="Price").ToArray();var accepted=prices.Where(x=>x.State=="Accepted"&&x.EvidenceHealthy).ToArray();
            var reference=references.GetValueOrDefault(row.CaptureId)?.GetValueOrDefault(row.Value.Code);
            result[i]=row with{EvidenceHealthy=row.EvidenceHealthy&&prices.All(x=>x.State!="NeedsReview")&&accepted.Length==1&&accepted[0].Id==reference&&accepted[0].Value.Close==row.Value.Close};
        }
        return result;
    }
    /// <summary>估值审核绑定当时实际价格修订；价格更正后旧快照证明失效，不继承旧比较授权。</summary>
    public void ReviewIndexSeries(string revision,bool accept,string reviewer,Guid? commandId=null)
    {
        if(string.IsNullOrWhiteSpace(reviewer)||reviewer.Length>120||reviewer.Any(char.IsControl))throw new InvalidDataException("指数审核人无效。");
        var payload=ExitAlertEvaluator.Hash(JsonSerializer.Serialize(new{revision,accept,reviewer}));if(IndexReplay(commandId,"index-series-review",payload) is not null)return;
        _session.BeforeWrite?.Invoke();using var transaction=_session.Connection.BeginTransaction();var now=IndexWriteAt();var text=_session.Scalar("SELECT day FROM nt_index_revision WHERE id=$id",("$id",revision)) as string??throw new InvalidDataException("指数修订不存在。");
        var date=DateOnly.ParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture);var all=IndexSeriesRevisions(date,date,now);var target=all.Single(x=>x.Id==revision);
        if(target.State!="NeedsReview"||!target.EvidenceHealthy)throw new InvalidDataException("指数修订不在可审核状态或证明不健康。");
        if(accept)foreach(var prior in all.Where(x=>x.Value.Code==target.Value.Code&&x.Value.Kind==target.Value.Kind&&x.State=="Accepted"))IndexEvent(prior.Id,"Superseded",reviewer,now);
        IndexEvent(target.Id,accept?"Accepted":"Rejected",reviewer,now);RecordIndexCommand(commandId,"index-series-review",payload,"true",now);_session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    private void IndexEvent(string revision,string state,string reviewer,DateTimeOffset at)=>_session.Execute("INSERT INTO nt_index_event(revision_id,state,recorded_at,reviewer) VALUES($id,$state,$at,$reviewer)",("$id",revision),("$state",state),("$at",IndustryTime(at)),("$reviewer",reviewer));
    private DateTimeOffset IndexWriteAt()
    {
        var now=_session.Clock.GetUtcNow();var latest=_session.Scalar("SELECT at FROM (SELECT first_seen AS at FROM nt_index_revision UNION ALL SELECT recorded_at AS at FROM nt_index_event UNION ALL SELECT completed_at AS at FROM nt_index_command UNION ALL SELECT retrieved_utc AS at FROM source_fetch) ORDER BY julianday(at) DESC LIMIT 1") as string;
        if(latest is not null&&DateTimeOffset.Parse(latest,CultureInfo.InvariantCulture)>now)throw new InvalidDataException("指数登记时钟倒退，拒绝新增或改写知识时点。");return now;
    }
    private string? IndexReplay(Guid? id,string operation,string? payload)
    {
        if(id is not {} value)return null;var global=_session.Scalar("SELECT operation FROM worker_business_command WHERE request=$id",("$id",value.ToString("N"))) as string;
        using var command=_session.Command("SELECT operation,payload_hash,result_json FROM nt_index_command WHERE id=$id",("$id",value.ToString("N")));using var reader=command.ExecuteReader();
        if(!reader.Read())return global is null?null:throw new InvalidDataException("指数命令双回执缺损。");
        if(global!=operation||reader.GetString(0)!=operation||payload is not null&&reader.GetString(1)!=payload)throw new InvalidDataException("指数命令不能换操作或负载。");return reader.GetString(2);
    }
    private void RecordIndexCommand(Guid? id,string operation,string payload,string result,DateTimeOffset at)
    {
        if(id is not {} value)return;_session.Execute("INSERT INTO nt_index_command VALUES($id,$operation,$payload,$result,$at)",("$id",value.ToString("N")),("$operation",operation),("$payload",payload),("$result",result),("$at",IndustryTime(at)));_session.RecordCommand(id,operation);
    }
}
