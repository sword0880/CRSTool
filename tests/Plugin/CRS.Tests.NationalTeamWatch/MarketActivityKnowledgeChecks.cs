using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static partial class MarketActivityKnowledgeChecks
{
    private static readonly DateOnly Day=new(2026,10,20);
    private static readonly DateTimeOffset Start=new(2026,10,27,6,0,0,TimeSpan.Zero);
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);Temporal(root);Legacy(Path.Combine(root,"legacy"));Corruption(Path.Combine(root,"corruption"));Rollback(Path.Combine(root,"rollback"));
        await Jobs(Path.Combine(root,"jobs"));await MarketActivityKnowledgeModelChecks.RunAsync(JsonSerializer.Deserialize<MarketActivityKnowledgeReport>(File.ReadAllText(Path.Combine(root,"snapshot.json")))!);
        Console.WriteLine("PASS 成交知识专项；全部为隔离合成资料，不代表正式服务、历史公开可用性或机构成交。输出="+root);
    }
    private static void Temporal(string root)
    {
        var clock=new Clock(Start);var path=Path.Combine(root,"data");var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);
        using(var repo=TestMarketData.Open(path,raw,timeProvider:clock))
        {
            repo.SaveMarketActivity("SSE",Day,Source("SSE",100));var first=repo.MarketActivityKnowledge(Day,Day,null);var firstId=first.Revisions.Single().Id;
            Check(first.Revisions.Single() is{KnowledgeOrigin:"Native",Decision:"Observed",State:MarketCycleObservationState.Accepted,SourcePublishedAt:null}&&first.Revisions[0].FirstSeenAt==Start,"新事实记录实际系统登记时点，不伪造官方发布时间");
            Check(first.Cutoff.Offset==TimeSpan.FromHours(8)&&first.Revisions.All(x=>x.FirstSeenAt.Offset==TimeSpan.FromHours(8)&&x.StateRecordedAt.Offset==TimeSpan.FromHours(8)&&x.SourceRetrievedAt.Offset==TimeSpan.FromHours(8)),"报告读入边界统一北京时间，界面直接格式化不会误标UTC");
            Check(repo.MarketActivityKnowledge(Day,Day,Start.AddTicks(-1)).Revisions.Count==0,"取得或登记前不可见，不把当前值带回过去");
            clock.Now=Start.AddHours(1);repo.SaveMarketActivity("SZSE",Day,Source("SZSE",200));repo.SaveMarketActivity("SSE",Day,Source("SSE",100));
            Check(repo.MarketActivityKnowledge(Day,Day,null).Revisions.Count==2&&repo.MarketActivityKnowledge(Day,Day,null).Revisions.Single(x=>x.Market=="SSE").FirstSeenAt==Start,"两市分别保存，重复原件不重置首次可知时刻");
            var beforeConflict=repo.MarketActivityKnowledge(Day,Day,clock.Now);
            clock.Now=Start.AddHours(2);repo.SaveMarketActivity("SSE",Day,Source("SSE",101));var conflict=repo.MarketActivityKnowledge(Day,Day,null);
            Check(conflict.Revisions.Count(x=>x.State==MarketCycleObservationState.NeedsReview)==2&&conflict.Revisions.Single(x=>x.Market=="SZSE").State==MarketCycleObservationState.Accepted,"同市场不同值先阻断，该市场冲突不篡改另一市场原件");
            var selected=conflict.Revisions.Single(x=>x.Market=="SSE"&&x.AmountYuan==101).Id;
            clock.Now=Start.AddHours(3);repo.ReviewMarketActivity(selected,true,"synthetic-reviewer",Guid.NewGuid());var accepted=repo.MarketActivityKnowledge(Day,Day,null);
            Check(accepted.Revisions.Single(x=>x.Id==firstId).State==MarketCycleObservationState.Rejected&&accepted.Revisions.Single(x=>x.Id==selected).State==MarketCycleObservationState.Accepted,"接受所选值与竞争值否决逐条进入同一事件历史");
            Check(Convert.ToInt32(Scalar(path,"SELECT count(*) FROM market_activity_review_audit WHERE fetch_id=$id",firstId))==1,"竞争值否决也进入既有审核审计，不仅改当前指针");
            var retrospective=repo.MarketActivityKnowledge(Day,Day,Start.AddHours(2));
            Check(retrospective.Revisions.Count(x=>x.State==MarketCycleObservationState.NeedsReview)==2,"早于审核的截止时点仍显示当时冲突");
            Check(repo.MarketActivityKnowledge(Day,Day,Start.AddHours(1)).Fingerprint==beforeConflict.Fingerprint,"未来补入和审核不改变较早截止的完整金融指纹");
            var old=repo.MarketActivityKnowledge(Day,Day,Start);Check(old.Revisions.Count==1&&old.Revisions[0].State==MarketCycleObservationState.Accepted,"当前已经否决的版本可在更早知识截面还原当时状态");
            Check(repo.MarketActivityKnowledge(Day,Day,Start.ToOffset(TimeSpan.FromHours(8))).Fingerprint==old.Fingerprint,"等价UTC和北京时间截止身份一致");
            var frozen=repo.MarketActivityKnowledge(Day,Day,clock.Now);clock.Now=Start.AddHours(4);repo.SaveMarketActivity("SSE",Day,Source("SSE",102));
            Check(repo.MarketActivityKnowledge(Day,Day,null).Revisions.Count(x=>x.State==MarketCycleObservationState.NeedsReview)==2&&repo.MarketActivityKnowledge(Day,Day,Start.AddHours(3)).Fingerprint==frozen.Fingerprint,"后续新修订重新触发当前冲突，同时保留过去已接受截面");
            File.WriteAllText(Path.Combine(root,"snapshot.json"),JsonSerializer.Serialize(repo.MarketActivityKnowledge(Day,Day,null),new JsonSerializerOptions{WriteIndented=true}));
            Reject(()=>repo.MarketActivityKnowledge(Day,Day,clock.Now.AddSeconds(1)),"未来知识截止拒绝");
            Reject(()=>repo.MarketActivityKnowledge(Day,Day.AddDays(-1),null),"颠倒区间拒绝");
            Reject(()=>repo.MarketActivityKnowledge(Day.AddDays(-401),Day,null),"超过400日区间拒绝");
            Check(repo.MarketActivityKnowledge(Day.AddDays(-1),Day.AddDays(-1),null).Revisions.Count==0,"真实无登记仅为空截面，不生成零成交事实");
            var cancelled=false;try{repo.Read(r=>r.MarketActivityKnowledge(Day,Day,null),new(true));}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"取消读取不返回空成功");
            var hash=old.Revisions.Single().EvidenceHash;var file=Path.Combine(rawPath,hash+".raw");var bytes=File.ReadAllBytes(file);File.WriteAllText(file,"corrupt");
            Check(repo.Read(r=>r.MarketActivityKnowledge(Day,Day,Start),default).Revisions.Single().State==MarketCycleObservationState.Critical,"未巡检的原件损坏也不能用于较早知识截面计算");
            repo.ReconcileEvidenceIntegrity();File.WriteAllBytes(file,bytes);
            Check(repo.MarketActivityKnowledge(Day,Day,Start).Revisions.Single().State==MarketCycleObservationState.Critical,"原字节恢复后仍需解除Q15隔离");
            repo.ReconcileEvidenceIntegrity();Check(repo.MarketActivityKnowledge(Day,Day,Start).Fingerprint==old.Fingerprint,"原件恢复与解除隔离后旧知识截面原样恢复");
            clock.Now=Start.AddHours(2);var blocked=false;try{repo.ReviewMarketActivity(selected,true,"synthetic-reviewer",Guid.NewGuid());}catch(InvalidDataException){blocked=true;}Check(blocked,"登记时钟倒退不能改写审核历史");clock.Now=Start.AddHours(5);
        }
        using var restart=TestMarketData.Open(path,raw,timeProvider:clock);Check(restart.MarketActivityKnowledge(Day,Day,Start).Revisions.Single().FirstSeenAt==Start,"普通SQLite重启不重写已记录的首次可知时刻");
    }
    private static void Legacy(string root)
    {
        var clock=new Clock(Start);var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using(var seed=TestMarketData.Open(data,raw,timeProvider:clock)){seed.SaveMarketActivity("SSE",Day,Source("SSE",100));seed.ReviewMarketActivity(seed.MarketActivityRevisions().Single().Id,false,"legacy-reviewer");}
        // 只在合成库移除新版本元数据，保留旧观察、原件与审核表以构造真实旧版结构。
        Sql(data,"DROP TABLE market_activity_state_event; DROP TABLE market_activity_registration; DELETE FROM feature_schema_migration WHERE feature='market-activity-knowledge';");
        clock.Now=Start.AddDays(1);
        using(var migrated=TestMarketData.Open(data,raw,timeProvider:clock))
        {
            Check(migrated.MarketActivityKnowledge(Day,Day,Start.AddHours(1)).Revisions.Count==0,"旧资料不根据来源取得日期伪造历史系统可知性");
            var row=migrated.MarketActivityKnowledge(Day,Day,null).Revisions.Single();
            Check(row.KnowledgeOrigin=="LegacyObservedNow"&&row.FirstSeenAt==clock.Now&&row.SourceRetrievedAt==Start&&row.State==MarketCycleObservationState.Rejected,"迁移保留旧来源时刻及当前否决状态，首次可知从真实迁移起算");
        }
        clock.Now=clock.Now.AddHours(1);using var reopened=TestMarketData.Open(data,raw,timeProvider:clock);
        Check(reopened.MarketActivityKnowledge(Day,Day,null).Revisions.Single().FirstSeenAt==Start.AddDays(1),"重启不会重复推进旧资料基线时刻");
    }
    private static MarketActivitySource Source(string market,decimal amount)=>new(OfficialMarketActivitySource.Request(market,Day),Encoding.UTF8.GetBytes(market+"|"+amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),market,Day,new(market,Day,amount,amount*10));
    private static void Sql(string data,string sql,string? id=null)
    {using var db=Connection(data);using var command=db.CreateCommand();command.CommandText=sql;if(id is not null)command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();}
    private static object? Scalar(string data,string sql,string? id=null)
    {using var db=Connection(data);using var command=db.CreateCommand();command.CommandText=sql;if(id is not null)command.Parameters.AddWithValue("$id",id);return command.ExecuteScalar();}
    private static SqliteConnection Connection(string data){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false,ForeignKeys=false}.ToString());db.Open();return db;}
    private sealed class Clock(DateTimeOffset now):TimeProvider{public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now;}
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 成交知识："+text);}
    private static void Reject(Action work,string text){try{work();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("未拒绝："+text);}
}
