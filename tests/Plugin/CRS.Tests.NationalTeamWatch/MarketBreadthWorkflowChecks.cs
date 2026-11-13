using System.Text;
using System.Text.Json;
using CRS.Common.Integrity;
using CRS.IO.Staging;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class MarketBreadthWorkflowChecks
{
    private static readonly DateOnly Day=new(2026,10,20);
    private static readonly DateTimeOffset Start=new(2026,10,28,6,0,0,TimeSpan.Zero);
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var clock=new Clock(Start);var path=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using var repo=TestMarketData.Open(path,raw,timeProvider:clock);
        var original=Encoding.UTF8.GetBytes("synthetic original breadth");var archive=new MarketBreadthArchive("reviewed-breadth-v1",Day,"SSE-SZSE-Ashares-point-in-time-v1","synthetic-universe",Day,"BackAdjustedToObservationDate",["SSE:600000","SZSE:000001"],[new("SSE:600000",10,9,9,8),new("SZSE:000001",7,9,9,7)]);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(archive);var first=Submission(original,bytes);var request=Guid.NewGuid();var id=repo.SubmitMarketBreadth(original,bytes,first,request);var pending=repo.Read(r=>r.MarketBreadthHistory(Day,Day,null),default);
        File.WriteAllText(Path.Combine(root,"pending.json"),JsonSerializer.Serialize(pending));
        Check(pending.Rows.Single() is{Decision:"NeedsReview",State:MarketCycleObservationState.NeedsReview,StateCommandId:not null},"登记不自动批准，真实登记命令进入初始事件");
        Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Single().State==MarketCycleObservationState.NeedsReview,"待核验档案不能成为周期有效输入");
        Check(repo.MarketBreadthSubmissionReceipt(request,first)?.RevisionId==id,"业务回执绑定原请求、原件和标准化摘要");
        Check(repo.SubmitMarketBreadth(original,bytes,first,request)==id&&repo.MarketBreadthHistory(Day,Day,null).Rows.Count==1,"重放登记不新增档案或初始事件");
        Reject(()=>repo.SubmitMarketBreadth(original,bytes,first with{Reason="changed"},request),"同请求换参数拒绝");
        Reject(()=>repo.ReviewMarketBreadth(new(id,Day,pending.StateFingerprint,true,"synthetic-reviewer","fixture",false),Guid.NewGuid()),"未经原件确认不能通过");
        clock.Now=Start.AddHours(1);var accept=new MarketBreadthReview(id,Day,pending.StateFingerprint,true,"synthetic-reviewer","原件、当日名单及复权口径已核对",true);var acceptedRequest=Guid.NewGuid();repo.ReviewMarketBreadth(accept,acceptedRequest);
        var accepted=repo.MarketBreadthHistory(Day,Day,null);Check(accepted.Rows.Single() is{Decision:"Accepted",State:MarketCycleObservationState.Accepted}&&accepted.Rows[0].StateCommandId==acceptedRequest.ToString("N"),"明确审核后才参与研究，保留因果命令");
        Check(repo.MarketBreadthHistory(Day,Day,Start).StateFingerprint==pending.StateFingerprint,"后来的批准不回填过去知识截面");
        repo.ReviewMarketBreadth(accept,acceptedRequest);Check(repo.MarketBreadthHistory(Day,Day,null).StateFingerprint==accepted.StateFingerprint,"审核原请求重放不追加事件");
        Reject(()=>repo.ReviewMarketBreadth(accept with{Reason="changed"},acceptedRequest),"审核请求不能更换说明");
        clock.Now=Start.AddHours(2);var changed=archive with{Observations=[new("SSE:600000",11,9,9,8),new("SZSE:000001",7,9,9,7)]};var nextBytes=JsonSerializer.SerializeToUtf8Bytes(changed);var second=Submission(original,nextBytes);var secondId=repo.SubmitMarketBreadth(original,nextBytes,second,Guid.NewGuid());
        var conflict=repo.MarketBreadthHistory(Day,Day,null);Check(conflict.Rows.Count==2&&conflict.Rows.Count(x=>x.Decision=="NeedsReview")==1&&repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==2,"新修订触发竞争，旧已核版保留而完整周期信号关闭");
        File.WriteAllText(Path.Combine(root,"conflict.json"),JsonSerializer.Serialize(conflict));
        Reject(()=>repo.ReviewMarketBreadth(new(secondId,Day,accepted.StateFingerprint,true,"synthetic-reviewer","stale",true),Guid.NewGuid()),"其他窗口新资料出现后旧摘要不能批准");
        clock.Now=Start.AddHours(3);var selectedRequest=Guid.NewGuid();repo.ReviewMarketBreadth(new(secondId,Day,conflict.StateFingerprint,true,"synthetic-reviewer","选择新原件版本",true),selectedRequest);
        var selected=repo.MarketBreadthHistory(Day,Day,null);Check(selected.Rows.Single(x=>x.Id==id).Decision=="Superseded"&&selected.Rows.Single(x=>x.Id==secondId).Decision=="Accepted"&&selected.Rows.All(x=>x.StateCommandId==selectedRequest.ToString("N")),"选择新版本和替代旧版在同一事务追加相同命令的事件");
        File.WriteAllText(Path.Combine(root,"selected.json"),JsonSerializer.Serialize(selected));
        Check(repo.MarketBreadthHistory(Day,Day,Start.AddHours(2)).StateFingerprint==conflict.StateFingerprint,"较早冲突截面原样保留");
        Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Single().RevisionId==secondId,"周期只采用明确选定版本");
        Check(repo.MarketBreadthEvidence(secondId,true)==first.Original.Sha256&&repo.MarketBreadthEvidence(secondId,false)==second.Normalized.Sha256,"原件导出哈希各自绑定真实档案");
        clock.Now=Start.AddHours(4);var rejectRequest=Guid.NewGuid();repo.ReviewMarketBreadth(new(secondId,Day,selected.StateFingerprint,false,"synthetic-reviewer","撤销当前核验",false),rejectRequest);
        Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==0&&repo.MarketBreadthHistory(Day,Day,null).Rows.Count==2,"否决当前版本不自动复活已替代旧版，不删除历史");
        var beforeFailure=repo.MarketBreadthHistory(Day,Day,null);clock.Now=Start.AddHours(5);
        Mutate(path,"CREATE TRIGGER fail_breadth_review BEFORE INSERT ON nt_breadth_event WHEN NEW.state='Accepted' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
        var failed=false;try{repo.ReviewMarketBreadth(new(secondId,Day,beforeFailure.StateFingerprint,true,"synthetic-reviewer","restore",true),Guid.NewGuid());}catch(SqliteException){failed=true;}
        Check(failed&&repo.MarketBreadthHistory(Day,Day,null).StateFingerprint==beforeFailure.StateFingerprint,"事件写入失败时状态、链头和成功回执一起回滚");Mutate(path,"DROP TRIGGER fail_breadth_review");
        var proofFile=Path.Combine(root,"raw",second.Normalized.Sha256+".raw");var proofBytes=File.ReadAllBytes(proofFile);File.WriteAllText(proofFile,"corrupt");var damaged=repo.MarketBreadthHistory(Day,Day,null);
        Reject(()=>repo.ReviewMarketBreadth(new(secondId,Day,damaged.StateFingerprint,true,"synthetic-reviewer","damaged",true),Guid.NewGuid()),"原件损坏不能批准");File.WriteAllBytes(proofFile,proofBytes);
        var preserved=repo.MarketBreadthHistory(Day,Day,null);File.WriteAllText(Path.Combine(root,"history.json"),JsonSerializer.Serialize(preserved));
        var oldArchive=archive with{Date=Day.AddDays(-100),UniverseDate=Day.AddDays(-100)};var oldBytes=JsonSerializer.SerializeToUtf8Bytes(oldArchive);var oldId=repo.SubmitMarketBreadth(original,oldBytes,Submission(original,oldBytes),Guid.NewGuid());
        Mutate(path,"UPDATE nt_breadth_archive SET proof_json='corrupt-old-proof' WHERE id='"+oldId+"'");
        Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==0,"当前评估不扫描窗口外的逐证券旧档案，历史损坏不挤占当前查询");
        Mutate(path,"DELETE FROM nt_breadth_event WHERE sequence=(SELECT max(sequence) FROM nt_breadth_event WHERE archive_id='"+secondId+"')");Reject(()=>repo.MarketBreadthHistory(Day,Day,null),"删除最新审核事件不能回退到旧状态");
        await MarketBreadthModelChecks.RunAsync(preserved);
        Console.WriteLine("PASS 广度工作流专项：合成原件与普通SQLite，不代表真实全市场广度验收。");
    }
    internal static MarketBreadthSubmission Submission(byte[] original,byte[] normalized)=>new(new(Guid.NewGuid(),ContentFingerprint.Sha256(original),original.Length,"market-breadth-original-v1"),new(Guid.NewGuid(),ContentFingerprint.Sha256(normalized),normalized.Length,"market-breadth-normalized-v1"),"https://example.invalid/breadth",Start,"synthetic-submitter","合成资料登记");
    private sealed class Clock(DateTimeOffset value):TimeProvider{public DateTimeOffset Now=value;public override DateTimeOffset GetUtcNow()=>Now;}
    private static void Mutate(string data,string sql){using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false,ForeignKeys=false}.ToString());db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 广度工作流："+text);}
    private static void Reject(Action action,string text){try{action();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
