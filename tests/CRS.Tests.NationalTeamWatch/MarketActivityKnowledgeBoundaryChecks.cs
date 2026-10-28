using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Serialization;
using Microsoft.Data.Sqlite;

internal static partial class MarketActivityKnowledgeChecks
{
    private static void Corruption(string root)
    {
        var cases=new[]{
            ("financial-value","UPDATE market_activity_observation SET amount_yuan='999' WHERE fetch_id=$id"),
            ("invalid-number","UPDATE market_activity_observation SET amount_yuan='invalid' WHERE fetch_id=$id"),
            ("source-time","UPDATE source_fetch SET retrieved_utc='invalid' WHERE fetch_id=$id"),
            ("missing-timezone","UPDATE source_fetch SET retrieved_utc='2026-10-27T14:00:00' WHERE fetch_id=$id"),
            ("source-hash","UPDATE source_fetch SET original_sha256='invalid' WHERE fetch_id=$id"),
            ("missing-source","DELETE FROM source_fetch WHERE fetch_id=$id"),
            ("registration-time","UPDATE market_activity_registration SET registered_at='2026-10-01T00:00:00Z' WHERE fetch_id=$id"),
            ("missing-registration","DELETE FROM market_activity_registration WHERE fetch_id=$id"),
            ("event-hash","UPDATE market_activity_state_event SET actor='changed' WHERE fetch_id=$id AND cause='ExplicitReview'"),
            ("event-decision","UPDATE market_activity_state_event SET decision='Rejected' WHERE fetch_id=$id AND cause='ExplicitReview'"),
            ("missing-tail","DELETE FROM market_activity_state_event WHERE sequence=(SELECT max(sequence) FROM market_activity_state_event WHERE fetch_id=$id)"),
            ("missing-genesis","DELETE FROM market_activity_state_event WHERE fetch_id=$id AND cause='SourceObserved'"),
            ("head-pointer","UPDATE market_activity_registration SET head_hash='bad' WHERE fetch_id=$id"),
            ("review-pointer","UPDATE market_activity_review SET decision='Rejected' WHERE fetch_id=$id"),
            ("feature-version","UPDATE feature_schema_migration SET version=99 WHERE feature='market-activity-knowledge'")};
        foreach(var (name,mutation) in cases)
        {
            var clock=new Clock(Start);var data=Path.Combine(root,name,"data");var raw=new RawEvidenceStore(Path.Combine(root,name,"raw"));using var repo=TestMarketData.Open(data,raw,timeProvider:clock);
            repo.SaveMarketActivity("SSE",Day,Source("SSE",100));var id=repo.MarketActivityRevisions().Single().Id;clock.Now=clock.Now.AddMinutes(1);repo.ReviewMarketActivity(id,true,"fixture");
            Sql(data,mutation,id);Reject(()=>repo.Read(r=>r.MarketActivityKnowledge(Day,Day,null),default),"金融值、原件及事件损坏失败关闭："+name);
        }
        foreach(var (name,mutation) in new[]{("missing-table","DROP TABLE market_activity_state_event"),("missing-migration","DELETE FROM feature_schema_migration WHERE feature='market-activity-knowledge'"),("unknown-version","UPDATE feature_schema_migration SET version=99 WHERE feature='market-activity-knowledge'")})
        {
            var data=Path.Combine(root,"schema-"+name,"data");var raw=new RawEvidenceStore(Path.Combine(root,"schema-"+name,"raw"));using(var seed=TestMarketData.Open(data,raw)){}
            Sql(data,mutation);Reject(()=>{using var reopen=TestMarketData.Open(data,raw);},"已迁移结构缺损不自动重建："+name);
        }
    }
    private static void Rollback(string root)
    {
        var clock=new Clock(Start);var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var repo=TestMarketData.Open(data,raw,timeProvider:clock);
        repo.SaveMarketActivity("SSE",Day,Source("SSE",100));clock.Now=Start.AddHours(1);repo.SaveMarketActivity("SSE",Day,Source("SSE",101));
        var selected=repo.MarketActivityRevisions().Single(x=>x.AmountYuan==101).Id;var request=Guid.NewGuid();clock.Now=Start.AddHours(2);
        Sql(data,"CREATE TRIGGER fail_activity_event BEFORE INSERT ON market_activity_state_event WHEN NEW.cause='ExplicitReview' BEGIN SELECT RAISE(ABORT,'fixture rollback'); END;");
        var failed=false;try{repo.ReviewMarketActivity(selected,true,"fixture",request);}catch(SqliteException){failed=true;}
        Check(failed&&repo.MarketActivityKnowledge(Day,Day,null).Revisions.All(x=>x.State==MarketCycleObservationState.NeedsReview)
            &&Convert.ToInt32(Scalar(data,"SELECT count(*) FROM market_activity_review_audit"))==0&&Convert.ToInt32(Scalar(data,"SELECT count(*) FROM worker_business_command WHERE request=$id",request.ToString("N")))==0,"所选审核事件失败时竞争否决、当前指针、审计与成功回执一起回滚");
        Sql(data,"DROP TRIGGER fail_activity_event");repo.ReviewMarketActivity(selected,true,"fixture",request);
        Check(repo.MarketActivityKnowledge(Day,Day,null).Revisions.Single(x=>x.Id==selected).State==MarketCycleObservationState.Accepted
            &&Convert.ToInt32(Scalar(data,"SELECT count(*) FROM market_activity_state_event WHERE command_id=$id",request.ToString("N")))==2,"故障恢复可用原请求提交两条相互关联审核事件");
    }
    private static async Task Jobs(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));DateTimeOffset earliest;
        using(var seed=TestMarketData.Open(Path.Combine(root,"data"),raw)){seed.SaveMarketActivity("SSE",Day,Source("SSE",100));seed.SaveMarketActivity("SZSE",Day,Source("SZSE",200));earliest=seed.MarketActivityKnowledge(Day,Day,null).Revisions.Min(x=>x.FirstSeenAt);}
        var gateway=new NoNetwork();await using var session=new NationalTeamBackgroundSession(new Context(root),gateway);await session.StartAsync(default);
        var query=new EtfRequest("activity-knowledge",From:Day,To:Day,Count:1);var blocks=new List<JsonDocumentBlock>();
        while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(query),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;query=query with{Offset=next,Snapshot=page.Snapshot};}
        var report=JsonDocumentBlocks.Decode<MarketActivityKnowledgeReport>(blocks,default);
        Check(report.Revisions.Count==2&&report.Fingerprint==MarketActivityKnowledgeReport.Freeze(Day,Day,report.Cutoff,report.Revisions).Fingerprint,"真实后台入口完整分块返回两市原件及稳定金融指纹");
        var earlier=JsonSerializer.Serialize(new EtfRequest("activity-knowledge",From:Day,To:Day,KnowledgeCutoff:earliest.AddTicks(-1)));
        var empty=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(earlier,default));
        Check(JsonDocumentBlocks.Decode<MarketActivityKnowledgeReport>(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(empty.Items)!,default).Revisions.Count==0&&gateway.Calls==0,"后台较早截面不带入新登记，查询不会联网或补写资料");
        Check(session.QueryOperation(earlier)=="activity-knowledge","知识时间读取纳入只读查询诊断白名单");
    }
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext{public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoNetwork:IPluginHttpGateway{public int Calls;public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token){Calls++;throw new Exception("成交知识查询禁止联网");}}
}
