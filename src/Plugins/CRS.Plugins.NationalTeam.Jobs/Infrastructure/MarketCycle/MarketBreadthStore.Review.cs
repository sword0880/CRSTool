using System.Globalization;
using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class MarketBreadthStore
{
    private sealed record StateEvent(string State,DateTimeOffset At,string Reviewer,string Reason,string? Command,string Previous,string Hash);
    private void InitializeReviewSchema()
    {
        var version=session.Scalar("SELECT version FROM feature_schema_migration WHERE feature='market-breadth-review'");
        var tables=Convert.ToInt32(session.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('nt_breadth_event','nt_breadth_head','nt_breadth_action')"));
        if(version is null&&tables!=0||version is not null&&(Convert.ToInt32(version)!=1||tables!=3))throw new InvalidDataException("广度审核事件结构不完整，拒绝重建。");
        if(version is not null)return;
        session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();
        session.Execute("""
            CREATE TABLE nt_breadth_event(sequence INTEGER PRIMARY KEY AUTOINCREMENT,archive_id TEXT NOT NULL REFERENCES nt_breadth_archive(id),state TEXT NOT NULL CHECK(state IN('Accepted','NeedsReview','Rejected','Superseded')),recorded_at TEXT NOT NULL,reviewer TEXT NOT NULL,reason TEXT NOT NULL,command_id TEXT,previous_hash TEXT NOT NULL,event_hash TEXT NOT NULL UNIQUE);
            CREATE TABLE nt_breadth_head(archive_id TEXT PRIMARY KEY REFERENCES nt_breadth_archive(id),head_hash TEXT NOT NULL);
            CREATE TABLE nt_breadth_action(id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,result_json TEXT NOT NULL,completed_at TEXT NOT NULL);
            CREATE INDEX ix_nt_breadth_event ON nt_breadth_event(archive_id,sequence);
            """);
        var old=new List<(string Id,Proof Proof)>();using(var command=session.Command("SELECT id,proof_json FROM nt_breadth_archive"))using(var reader=command.ExecuteReader())while(reader.Read())
        {var json=reader.GetString(1);if(Hash(json)!=reader.GetString(0))throw new InvalidDataException("旧广度登记证明损坏。");old.Add((reader.GetString(0),JsonSerializer.Deserialize<Proof>(json)??throw new InvalidDataException("旧广度证明为空。")));}
        // V1档案本身已包含明确核验人、说明和真实登记时间；只迁移其原有已核验状态，不推断未记录历史。
        foreach(var (id,proof) in old)AppendState(id,"Accepted",proof.RegisteredAt,proof.Reviewer,proof.Reason,null);
        session.Execute("INSERT INTO feature_schema_migration VALUES('market-breadth-review',1,$at)",("$at",Time(session.Clock.GetUtcNow())));transaction.Commit();
    }
    private static string EventHash(string id,string state,DateTimeOffset at,string reviewer,string reason,string? command,string previous)
        =>Hash(JsonSerializer.Serialize(new{Version=1,Id=id,State=state,At=at.ToUniversalTime(),Reviewer=reviewer,Reason=reason,Command=command,Previous=previous}));
    private void AppendState(string id,string state,DateTimeOffset at,string reviewer,string reason,string? command)
    {
        var previous=session.Scalar("SELECT head_hash FROM nt_breadth_head WHERE archive_id=$id",("$id",id)) as string??id;
        var hash=EventHash(id,state,at,reviewer,reason,command,previous);
        session.Execute("INSERT INTO nt_breadth_event(archive_id,state,recorded_at,reviewer,reason,command_id,previous_hash,event_hash) VALUES($id,$state,$at,$who,$reason,$command,$previous,$hash)",("$id",id),("$state",state),("$at",Time(at)),("$who",reviewer),("$reason",reason),("$command",command),("$previous",previous),("$hash",hash));
        session.Execute("INSERT INTO nt_breadth_head VALUES($id,$hash) ON CONFLICT(archive_id) DO UPDATE SET head_hash=excluded.head_hash",("$id",id),("$hash",hash));
    }
    private StateEvent ReadState(string id,DateTimeOffset registered,DateTimeOffset cutoff)
    {
        var previous=id;var lastAt=registered;StateEvent? selected=null;var count=0;
        using(var command=session.Command("SELECT state,recorded_at,reviewer,reason,command_id,previous_hash,event_hash FROM nt_breadth_event WHERE archive_id=$id ORDER BY sequence",("$id",id)))
        using(var reader=command.ExecuteReader())while(reader.Read())
        {
            session.OperationCancellation.ThrowIfCancellationRequested();var state=reader.GetString(0);var text=reader.GetString(1);var at=DateTimeOffset.ParseExact(text,"O",CultureInfo.InvariantCulture);var who=reader.GetString(2);var reason=reader.GetString(3);var request=reader.IsDBNull(4)?null:reader.GetString(4);var hash=reader.GetString(6);
            if(Time(at)!=text||at<lastAt||state is not ("Accepted" or "NeedsReview" or "Rejected" or "Superseded")||!Text(who,120)||!Text(reason,1000)||reader.GetString(5)!=previous||hash!=EventHash(id,state,at,who,reason,request,previous))throw new InvalidDataException("广度审核事件链不完整或被改写。");
            count++;previous=hash;lastAt=at;if(at<=cutoff)selected=new(state,at,who,reason,request,reader.GetString(5),hash);
        }
        if(count==0||selected is null||session.Scalar("SELECT head_hash FROM nt_breadth_head WHERE archive_id=$id",("$id",id)) as string!=previous)throw new InvalidDataException("广度状态记录或链头缺失。");return selected;
    }
    private void CheckReviewClock(DateTimeOffset now)
    {if(session.Scalar("SELECT max(recorded_at) FROM nt_breadth_event") is string latest&&DateTimeOffset.Parse(latest,CultureInfo.InvariantCulture)>now)throw new InvalidDataException("广度审核时钟倒退。");}
    private static string ImportPayload(string originalHash,string normalizedHash,string url,DateTimeOffset retrievedAt,string reviewer,string reason,bool reviewed)
    {
        var old=JsonSerializer.Serialize(new{originalHash,normalizedHash,Url=url,RetrievedAt=retrievedAt.ToUniversalTime(),reviewer,reason});
        return reviewed?Hash(old):Hash("market-breadth-submission-v1\n"+old);
    }
    public MarketBreadthHistory History(DateOnly from,DateOnly through,DateTimeOffset? requested)
    {
        var cutoff=requested??session.ReadSnapshotCutoff??session.Clock.GetUtcNow();
        if(from>through||through.DayNumber-from.DayNumber>400||cutoff==default||cutoff>session.Clock.GetUtcNow()||session.ReadSnapshotCutoff is {} snapshot&&cutoff>snapshot)throw new InvalidDataException("广度历史范围或知识截止无效。");
        return MarketBreadthHistory.Freeze(from,through,cutoff,Revisions(from,through,cutoff));
    }
    public string Submit(byte[] original,byte[] normalized,MarketBreadthSubmission submission,Guid command)
    {
        SubmissionPayload(submission);
        if(!ContentFingerprint.Matches(submission.Original.Sha256,original)||original.Length!=submission.Original.Length||!ContentFingerprint.Matches(submission.Normalized.Sha256,normalized)||normalized.Length!=submission.Normalized.Length)throw new InvalidDataException("广度暂存摘要与导入字节不符。");
        return Import(original,normalized,new(submission.SourceUrl),submission.RetrievedAt,submission.Submitter,submission.Reason,true,command,reviewed:false);
    }
    private static string SubmissionPayload(MarketBreadthSubmission value)
    {
        if(value.Original is null||value.Normalized is null||value.Original.Id==Guid.Empty||value.Normalized.Id==Guid.Empty||value.Original.Purpose!="market-breadth-original-v1"||value.Normalized.Purpose!="market-breadth-normalized-v1"
            ||!ContentFingerprint.IsSha256Hex(value.Original.Sha256)||!ContentFingerprint.IsSha256Hex(value.Normalized.Sha256)||value.Original.Length is <1 or >8388608||value.Normalized.Length is <1 or >8388608
            ||!Uri.TryCreate(value.SourceUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||value.SourceUrl.Length>700||!Text(value.Submitter,120)||!Text(value.Reason,240))throw new InvalidDataException("广度提交来源、用途或登记说明无效。");
        return ImportPayload(value.Original.Sha256,value.Normalized.Sha256,uri.AbsoluteUri,value.RetrievedAt,value.Submitter,value.Reason,false);
    }
    public MarketBreadthSubmissionReceipt? Receipt(Guid request,MarketBreadthSubmission submission)
    {
        var payload=SubmissionPayload(submission);string id;
        using(var command=session.Command("SELECT payload_hash,archive_id FROM nt_breadth_command WHERE id=$id",("$id",request.ToString("D"))))using(var reader=command.ExecuteReader())
        {if(!reader.Read())return null;if(reader.GetString(0)!=payload)throw new InvalidDataException("广度请求已绑定其他提交。");id=reader.GetString(1);}
        var json=session.Scalar("SELECT proof_json FROM nt_breadth_archive WHERE id=$id",("$id",id)) as string??throw new InvalidDataException("广度提交回执缺少档案。");
        if(Hash(json)!=id)throw new InvalidDataException("广度回执的登记证明损坏。");var proof=JsonSerializer.Deserialize<Proof>(json)??throw new InvalidDataException("广度回执为空。");
        return new(request,id,proof.Archive.Date,CRS.Timing.BeijingClock.Normalize(proof.RegisteredAt),proof.OriginalHash,proof.NormalizedHash);
    }
    public string Evidence(string revision,bool original)
    {
        var text=session.Scalar("SELECT day FROM nt_breadth_archive WHERE id=$id",("$id",revision)) as string??throw new InvalidDataException("广度档案不存在。");
        var day=DateOnly.ParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture);var row=History(day,day,null).Rows.SingleOrDefault(x=>x.Id==revision)??throw new InvalidDataException("广度档案不可见。");
        if(!row.EvidenceHealthy)throw new InvalidDataException("广度原件异常，不能导出为已核验字节。");return original?row.OriginalHash:row.NormalizedHash;
    }
    public void Review(MarketBreadthReview review,Guid request)
    {
        if(request==Guid.Empty||!Text(review.Reviewer,120)||!Text(review.Reason,240)||review.Accept&&!review.OriginalConfirmed)throw new InvalidDataException("广度核验需要明确核验人、说明及原件确认。");
        var payload=Hash(JsonSerializer.Serialize(review));
        using(var command=session.Command("SELECT payload_hash FROM nt_breadth_action WHERE id=$id",("$id",request.ToString("D"))))
        {if(command.ExecuteScalar() is string previous){if(previous!=payload)throw new InvalidDataException("广度审核命令已绑定其他参数。");return;}}
        session.BeforeWrite?.Invoke();using var transaction=session.Connection.BeginTransaction();var now=session.Clock.GetUtcNow();CheckReviewClock(now);
        var snapshot=History(review.Date,review.Date,now);if(snapshot.StateFingerprint!=review.ExpectedFingerprint)throw new InvalidDataException("广度档案已变化，请重新读取后核验。");
        var selected=snapshot.Rows.SingleOrDefault(x=>x.Id==review.RevisionId)??throw new InvalidDataException("广度修订不存在。");
        if(review.Accept&&!selected.EvidenceHealthy)throw new InvalidDataException("广度原件不健康，不能核验通过。");
        if(review.Accept)foreach(var other in snapshot.Rows.Where(x=>x.Id!=selected.Id&&x.Decision is "Accepted" or "NeedsReview"))AppendState(other.Id,other.Decision=="Accepted"?"Superseded":"Rejected",now,review.Reviewer,review.Reason,request.ToString("N"));
        AppendState(selected.Id,review.Accept?"Accepted":"Rejected",now,review.Reviewer,review.Reason,request.ToString("N"));
        session.Execute("INSERT INTO nt_breadth_action VALUES($id,$payload,$result,$at)",("$id",request.ToString("D")),("$payload",payload),("$result",JsonSerializer.Serialize(new{review.RevisionId,review.Accept,RecordedAt=now})),("$at",Time(now)));
        session.RecordCommand(request,"market-breadth-review");session.OperationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
}
