using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Serialization;
using Microsoft.Data.Sqlite;

internal static class MarketCycleStudyChecks
{
    private static readonly DateOnly Day=new(2026,10,20);
    private static readonly DateTimeOffset Start=new(2026,10,27,6,0,0,TimeSpan.Zero);
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"data");var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);var clock=new Clock(Start);
        SnapshotClock(Path.Combine(root,"snapshot-clock"));
        using(var repo=TestMarketData.Open(path,raw,timeProvider:clock))
        {
            var empty=repo.Read(r=>r.MarketCycleStudy(Day,null),default);
            Check(empty.Assessment is{DataStatus:MarketCycleDataStatus.InsufficientData,TopRiskLevel:MarketCycleSignalLevel.Unavailable,BottomCandidateLevel:MarketCycleSignalLevel.Unavailable}&&!empty.NotificationsEnabled,"正式只读仓储调用评估器，无资料不输出信号或通知");
            Reject(()=>repo.MarketCycleStudy(new(2026,10,27),null),"未结束的统计日拒绝");Reject(()=>repo.MarketCycleStudy(Day,Start.AddSeconds(1)),"未来知识截止拒绝");
            repo.SaveMarketActivity("SSE",Day,Activity("SSE",100));
            var missing=repo.MarketCycleStudy(Day,null);Check(missing.Input.ActivityPoints.Single() is{AmountYuan:null,State:MarketCycleObservationState.NeedsReview},"缺一个市场不能把单市金额当成市场总额");
            repo.SaveMarketActivity("SZSE",Day,Activity("SZSE",200));var both=repo.MarketCycleStudy(Day,null);
            Check(both.Input.ActivityPoints.Single() is{AmountYuan:300,VolumeShares:3000,State:MarketCycleObservationState.Accepted}&&both.Sources.Count(x=>x.Component=="Activity")==2,"两市各自核验后合计，真实原件引用分开保留");
            var before=both.Input.EvaluationCutoff;clock.Now=Start.AddHours(1);repo.SaveMarketActivity("SSE",Day,Activity("SSE",101));
            Check(repo.MarketCycleStudy(Day,null).Input.ActivityPoints.Single().AmountYuan is null,"新冲突关闭合计，不退回旧接受值");
            Check(repo.MarketCycleStudy(Day,before).Fingerprint==both.Fingerprint,"较晚成交冲突不改变较早冻结报告");
            var id=repo.MarketActivityRevisions().Single(x=>x.AmountYuan==101).Id;clock.Now=Start.AddHours(2);repo.ReviewMarketActivity(id,true,"synthetic-reviewer",Guid.NewGuid());
            Check(repo.MarketCycleStudy(Day,null).Input.ActivityPoints.Single().AmountYuan==301,"实际审核后采用新值，未将ETF份额或指数样本额混入");
            var original=Encoding.UTF8.GetBytes("synthetic reviewed breadth original");var archive=Archive();var bytes=JsonSerializer.SerializeToUtf8Bytes(archive);var request=Guid.NewGuid();
            var breadthId=repo.ImportReviewedMarketBreadth(original,bytes,new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","合成原件、当日名单与复权口径逐项核验",true,request);
            var saved=repo.MarketCycleStudy(Day,null);var breadth=saved.Input.BreadthPoints.Single();
            Check(breadth is{AboveMa20Count:1,AboveMa60Count:1,NewLowCount:1,UniverseCount:2,State:MarketCycleObservationState.Accepted},"后台按逐证券收盘、均线和前低计数，不接收前端比例");
            Check(repo.MarketCycleStudy(Day,before).Input.BreadthPoints.Count==0,"人工核验时刻之前不可使用后来广度");
            Check(repo.ImportReviewedMarketBreadth(original,bytes,new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","合成原件、当日名单与复权口径逐项核验",true,request)==breadthId,"同请求重放绑定原回执");
            repo.ImportReviewedMarketBreadth(original,bytes,new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","合成原件、当日名单与复权口径逐项核验",true,Guid.NewGuid());
            Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==1,"同档案重新提交不制造重复广度日期");
            Reject(()=>repo.ImportReviewedMarketBreadth(original,bytes,new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","changed",true,request),"原请求不能更换资料参数");
            Reject(()=>repo.ImportReviewedMarketBreadth(original,bytes,new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","fixture",false,Guid.NewGuid()),"未明确人工确认原件拒绝");
            Reject(()=>repo.ImportReviewedMarketBreadth(original,JsonSerializer.SerializeToUtf8Bytes(archive with{UniverseDate=Day.AddDays(1)}),new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","fixture",true,Guid.NewGuid()),"今天证券名单不能代替历史当日名单");
            Reject(()=>repo.ImportReviewedMarketBreadth(original,JsonSerializer.SerializeToUtf8Bytes(archive with{Members=["SSE:600000","SSE:600000"]}),new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","fixture",true,Guid.NewGuid()),"重复证券不能虚增分母");
            var file=Path.Combine(rawPath,breadth.EvidenceHash+".raw");var intact=File.ReadAllBytes(file);File.WriteAllText(file,"damaged");
            Check(repo.Read(r=>r.MarketCycleStudy(Day,null),default).Input.BreadthPoints.Single().State==MarketCycleObservationState.Critical,"查询现场验证广度原件，损坏立即关闭信号");File.WriteAllBytes(file,intact);
            foreach(var market in new[]{"SSE","SZSE"})foreach(var year in new[]{2025,2026})repo.RegisterReviewedCalendar(market,year,Encoding.UTF8.GetBytes(year==2025?GapRecoveryChecks.Calendar2025:DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source(market,year));
            var calendar=repo.Read(r=>r.MarketCycleStudy(Day,null),default);Check(calendar.Input.CalendarVerified&&calendar.Sources.Count(x=>x.Component=="Calendar")==4,"两市日历全部核验一致，并保留四份年度公告引用");
            Mutate(path,"UPDATE exchange_calendar_day SET is_open=1-is_open WHERE market='SSE' AND day='2026-10-20'");
            SeedUnverifiedPrice(path,raw,clock.Now);
            var unverifiedPrice=repo.Read(r=>r.MarketCycleStudy(Day,null),default);
            Check(unverifiedPrice.Input.IndexPoints.Single().State==MarketCycleObservationState.Critical&&unverifiedPrice.Assessment.TopRiskLevel==MarketCycleSignalLevel.Unavailable,"保存了价格行但方法原件不健康，不能冒充合格价格输入");
            Check(unverifiedPrice.Sources.Count(x=>x.Component=="Price")==3&&unverifiedPrice.Input.IndexPoints[0].EvidenceHash==unverifiedPrice.Sources.Single(x=>x.Component=="Price"&&x.Role=="Price").Hash,"实际价格适配器保留价格、方法、单位各原件，不把捕获ID当原件摘要");
            Check(unverifiedPrice.Input.ActivityPoints.Single().AmountYuan==301,"指数样本成交999999不会污染两市实际登记的301成交合计");
            Check(!repo.MarketCycleStudy(Day,null).Input.CalendarVerified,"日历表被改写但原件未变时重新解析发现差异");
            Mutate(path,"UPDATE exchange_calendar_day SET is_open=1-is_open WHERE market='SSE' AND day='2026-10-20'");
            var other=archive with{Observations=[new("SSE:600000",11,9,9,8),new("SZSE:000001",7,9,9,7)]};clock.Now=clock.Now.AddHours(1);
            repo.ImportReviewedMarketBreadth(original,JsonSerializer.SerializeToUtf8Bytes(other),new("https://example.invalid/reviewed-breadth"),Start,"synthetic-reviewer","另一合成修订",true,Guid.NewGuid());
            Check(repo.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==2&&repo.MarketCycleStudy(Day,null).Assessment.DataStatus==MarketCycleDataStatus.InsufficientData,"竞争广度档案并列保留，不静默择值或归一化");
            File.WriteAllText(Path.Combine(root,"missing.json"),JsonSerializer.Serialize(repo.MarketCycleStudy(Day,null)));
        }
        using(var reopened=TestMarketData.Open(path,raw,timeProvider:clock))Check(reopened.MarketCycleStudy(Day,null).Input.BreadthPoints.Count==2,"普通SQLite重启保留广度档案与真实核验时间");
        Mutate(path,"DROP TABLE nt_breadth_command");Reject(()=>{using var invalid=TestMarketData.Open(path,raw,timeProvider:clock);},"广度迁移表缺损不重建假空资料");
        var preview=MarketCycleChecks.StudyPreview();Check(preview.Assessment.DataStatus==MarketCycleDataStatus.ResearchPreview&&preview.Assessment.TimingProof=="NotProven"&&!preview.NotificationsEnabled,"完整合成输入可复算候选规则，仍不宣称择时验证");
        var blocks=JsonDocumentBlocks.Encode(preview,default);var replay=JsonDocumentBlocks.Decode<MarketCycleStudyReport>(blocks,default);
        Check(MarketCycleStudyReport.Freeze(replay.Input,replay.Sources,replay.ActivityKnowledgeFingerprint,replay.Rules).Fingerprint==preview.Fingerprint,"完整报告分块往返后冻结输入及结果可复算");
        File.WriteAllText(Path.Combine(root,"preview.json"),JsonSerializer.Serialize(preview));
        await Model(preview);await Background(Path.Combine(root,"background"));
        Console.WriteLine("PASS 周期接入专项：合成库及合成完整输入；不代表正式行情、来源许可或预测验证。");
    }
    private static MarketBreadthArchive Archive()=>new("reviewed-breadth-v1",Day,"SSE-SZSE-Ashares-point-in-time-v1","synthetic-universe",Day,"BackAdjustedToObservationDate",["SSE:600000","SZSE:000001"],[new("SSE:600000",10,9,9,8),new("SZSE:000001",7,9,9,7)]);
    private static void SnapshotClock(string root)
    {
        var clock=new Clock(Start);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var writer=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);
        var frozen=writer.Read(reader=>
        {
            clock.Now=Start.AddHours(1);writer.SaveMarketActivity("SSE",Day,Activity("SSE",100));
            return reader.MarketCycleStudy(Day,null);
        },default);
        Check(frozen.Input.EvaluationCutoff==Start&&frozen.Input.ActivityPoints.Count==0&&writer.MarketCycleStudy(Day,null).Input.ActivityPoints.Count==1,"并发写入发生在快照固定之后，默认知识截止仍取快照前时刻，不借用查询途中的较晚时间");
    }
    private static void SeedUnverifiedPrice(string path,RawEvidenceStore raw,DateTimeOffset at)
    {
        // 仅在隔离库插入不具备正式方法原件的价格捕获，验证读取适配器不会以存在代替健康。
        var definition=IndexResearchCatalog.Get("399317");var value=new IndexSeriesValue("399317",Day,"Price",100,SampleAmountYuan:999999,SampleVolumeShares:999999);
        var documents=new[]{("Price",OfficialIndexSeriesSource.PriceSource("399317",Day,Day).AbsoluteUri),("Units",OfficialIndexSeriesSource.UnitsSource.AbsoluteUri),("Method",definition.MethodUrl)}
            .Select(x=>new{Role=x.Item1,Url=x.Item2,Hash=raw.Save(Encoding.UTF8.GetBytes("synthetic unverified "+x.Item1)),RetrievedAt=at.ToUniversalTime()}).ToArray();
        var proof=JsonSerializer.Serialize(new{Kind="Price",Code="399317",From=Day,Through=Day,Documents=documents,PriceReferences=new Dictionary<string,string>(),Values=new[]{value}});
        var capture=CRS.Common.Integrity.ContentFingerprint.Sha256(Encoding.UTF8.GetBytes(proof));var revision="synthetic-price-revision";
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(path,"market.db"),Pooling=false,ForeignKeys=true}.ToString());db.Open();using var transaction=db.BeginTransaction();
        void Insert(string sql,params (string,object)[] args){using var command=db.CreateCommand();command.Transaction=transaction;command.CommandText=sql;foreach(var (name,val) in args)command.Parameters.AddWithValue(name,val);command.ExecuteNonQuery();}
        var day=Day.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);var time=at.ToUniversalTime().ToString("O");
        Insert("INSERT INTO nt_index_capture VALUES($id,'Price','399317',$day,$day,$method,$proof,$at)",("$id",capture),("$day",day),("$method",definition.MethodHash),("$proof",proof),("$at",time));
        foreach(var doc in documents)
        {
            var fetch=Guid.NewGuid().ToString("N");Insert("INSERT INTO source_fetch VALUES($id,'OfficialCnindexIndexSeries',$at,NULL,$url,$hash,$hash,'Accepted',$parser)",("$id",fetch),("$at",time),("$url",doc.Url),("$hash",doc.Hash),("$parser",OfficialIndexSeriesSource.ParserVersion));
            Insert("INSERT INTO nt_index_capture_fetch VALUES($capture,$role,$fetch)",("$capture",capture),("$role",doc.Role),("$fetch",fetch));
        }
        Insert("INSERT INTO nt_index_revision VALUES($id,'399317',$day,'Price',$json,1,$at,$capture)",("$id",revision),("$day",day),("$json",JsonSerializer.Serialize(value)),("$at",time),("$capture",capture));
        Insert("INSERT INTO nt_index_event(revision_id,state,recorded_at,reviewer) VALUES($id,'Accepted',$at,'synthetic')",("$id",revision),("$at",time));transaction.Commit();
    }
    private static MarketActivitySource Activity(string market,decimal amount)=>new(OfficialMarketActivitySource.Request(market,Day),Encoding.UTF8.GetBytes(market+amount),market,Day,new(market,Day,amount,amount*10));
    private static async Task Model(MarketCycleStudyReport report)
    {
        var workspace=new Workspace(report);var model=new MarketCycleStudyViewModel(workspace,default){AsOf=report.Input.AsOfDate.ToDateTime(TimeOnly.MinValue)};await model.ReadAsync();
        Check(model.Report is not null&&model.Status.Contains("择时能力未验证"),"页面仅展示后台冻结结果，完整输入仍标注研究预览");
        model.CutoffText="2026-10-27 14:00:00";Check(model.Report is null&&model.Sources.Count==0,"条件改变立即清空旧结果与来源");await model.ReadAsync();Check(workspace.Cutoff==Start,"界面按北京时间解析知识截止");
        workspace.Delayed=true;var first=model.ReadAsync();model.AsOf=model.AsOf!.Value.AddDays(-1);var second=model.ReadAsync();workspace.Replies[1].SetResult(report);await second;workspace.Replies[0].SetResult(report with{Sources=[]});await first;
        Check(model.Report==report&&!model.Busy,"慢旧请求不能覆盖新条件或忙碌状态");workspace.Delayed=false;workspace.Fail=true;await model.ReadAsync();Check(model.Report is null&&model.Status.Contains("不可用"),"查询失败清除旧信号，不显示旧结论");
    }
    private static async Task Background(string root)
    {
        await using var session=new NationalTeamBackgroundSession(new Context(root),new NoNetwork());await session.StartAsync(default);
        var request=new EtfRequest("market-cycle-study",To:Day,Count:1);var blocks=new List<JsonDocumentBlock>();
        while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;request=request with{Offset=next,Snapshot=page.Snapshot};}
        Check(JsonDocumentBlocks.Decode<MarketCycleStudyReport>(blocks,default).Assessment.DataStatus==MarketCycleDataStatus.InsufficientData&&session.QueryOperation(JsonSerializer.Serialize(request))=="market-cycle-study","后台正式查询入口分块返回缺口，纳入只读白名单且禁止联网");
    }
    private sealed class Workspace(MarketCycleStudyReport report):ResearchWorkspaceStub
    {
        public DateTimeOffset? Cutoff;public bool Delayed,Fail;public List<TaskCompletionSource<MarketCycleStudyReport>> Replies=[];
        public override Task<MarketCycleStudyReport> MarketCycleStudyAsync(DateOnly asOf,DateTimeOffset? cutoff,CancellationToken token)
        {Cutoff=cutoff;if(Fail)throw new IOException("fixture unavailable");if(!Delayed)return Task.FromResult(report);var reply=new TaskCompletionSource<MarketCycleStudyReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(reply);return reply.Task;}
    }
    private sealed class Clock(DateTimeOffset value):TimeProvider{public DateTimeOffset Now=value;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext{public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoNetwork:IPluginHttpGateway{public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("周期查询禁止联网");}
    private static void Mutate(string data,string sql){using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false,ForeignKeys=false}.ToString());db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 周期接入："+text);}
    private static void Reject(Action action,string text){try{action();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
