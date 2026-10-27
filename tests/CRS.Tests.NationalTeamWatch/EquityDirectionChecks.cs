using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Serialization;

internal static class EquityDirectionChecks
{
    private static readonly DateOnly Period=new(2026,6,30);
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);var data=Path.Combine(root,"data");
        using(var repo=TestMarketData.Open(data,raw))
        {
            Check(repo.EquityDirection(null) is{ReportAsOf:null,Positions.Count:0,Groups.Count:0},"没有披露时保持未知报告期，仍列观察范围缺口");
            DirectEquityChecks.Seed(repo);SeedSecond(repo);
            var report=repo.Read(r=>r.EquityDirection(Period),default);
            Check(report.DisclosedSecurities==2&&report.ReviewedAccounts==3&&report.Groups.All(x=>x.MarketValueCny is null&&x.SampleWeightPercent is null),"同报告期已核样本可读，没有行业和价格时不按股数计算权重");
            Check(report.ActiveSecurities==13&&report.Gaps.Count(x=>x.Code=="NoReviewedHoldingInPeriod")==11&&!report.WholePortfolioProven,"13只当前启用范围仅2只已核，缺失不是零持有或完整组合");
            Check(repo.EquityDirection(new(2026,3,31)) is{DisclosedSecurities:1,ReviewedAccounts:3},"历史报告期不混入另一股票的最新持仓");
            Reject(()=>repo.EquityDirection(new(2099,1,1)),"未来报告期拒绝");
            var bank=report.Positions.First(x=>x.Holding.Ticker=="600036");var other=report.Positions.Single(x=>x.Holding.Ticker=="600153");
            var one=Review(bank,"J","银行",10m);var two=Review(other,"C","制造业",.1m);var firstId=Guid.NewGuid();var secondId=Guid.NewGuid();
            Reject(()=>repo.ReviewEquityDirection(one with{OriginalConfirmed=false},Guid.NewGuid()),"不能继承持股核验来自动批准行业");
            Reject(()=>repo.ReviewEquityDirection(one with{DocumentHash=new('A',64)},Guid.NewGuid()),"错误原件摘要拒绝");
            Reject(()=>repo.ReviewEquityDirection(one with{ClosingPriceCny=-1},Guid.NewGuid()),"负价格拒绝");
            Reject(()=>repo.ReviewEquityDirection(one with{PriceDate=Period.AddDays(-1)},Guid.NewGuid()),"不以相邻日期或复权价格代替报告截止日口径");
            Reject(()=>repo.ReviewEquityDirection(one with{ClosingPriceCny=null},Guid.NewGuid()),"没有价格时不能遗留日期及价格页码");
            Reject(()=>repo.ReviewEquityDirection(one with{SchemeVersion=""},Guid.NewGuid()),"行业标准版本必须明确");
            repo.ReviewEquityDirection(one,firstId);repo.ReviewEquityDirection(one,firstId);
            Check(repo.EquityDirection(Period).ClassifiedSecurities==1&&repo.EquityDirection(Period).Groups.Where(x=>x.Institution=="汇金资管").All(x=>x.SampleWeightPercent is null),"部分样本缺价时不将剩余部分重新归一为100%");
            Reject(()=>repo.ReviewEquityDirection(one with{SectorName="修改参数"},firstId),"已提交请求不能换参数重放");
            Reject(()=>repo.ReviewEquityDirection(one,Guid.NewGuid()),"更正必须明确引用当前方向核验基线");
            repo.ReviewEquityDirection(two,secondId);report=repo.Read(r=>r.EquityDirection(null),default);
            var asset=report.Groups.Where(x=>x.Institution=="汇金资管").ToArray();
            Check(report.ReportAsOf==Period&&report.ClassifiedSecurities==2&&report.PricedSecurities==2,"自动期选择同一个最新已核报告期，原件方向/期末价完整接入");
            Check(asset.Single(x=>x.SectorCode=="J").MarketValueCny==9000m&&asset.Single(x=>x.SectorCode=="C").MarketValueCny==900m
                &&Math.Abs(asset.Single(x=>x.SectorCode=="J").SampleWeightPercent!.Value-100m*9000m/9900m)<.00001m,"大股数低价格与小股数高价格按人民币市值计算，不能反转主要行业");
            Check(report.Groups.Single(x=>x.Institution=="社保基金").SampleWeightPercent==100m&&report.Positions.Count==3,"具体社保组合不并入汇金，跨证券同账户只各计一次");
            File.WriteAllText(Path.Combine(root,"snapshot.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            var facts=report.Positions.Select(x=>x.Fact!).Distinct().ToArray();
            var separate=EquityDirectionProjection.Build(Period,report.ObservedAt,repo.EquityWatchlist(),report.Positions.Select(x=>x.Holding.Ticker=="600153"?x.Holding with{Nature="Strategic"}:x.Holding).ToArray(),facts,[]);
            Check(separate.Groups.Where(x=>x.Institution=="汇金资管").All(x=>x.SampleWeightPercent==100m),"战略与证券投资独立分母，不混成同一配置权重");
            var mixed=EquityDirectionProjection.Build(Period,report.ObservedAt,repo.EquityWatchlist(),report.Positions.Select(x=>x.Holding).ToArray(),facts.Select(x=>x.DisclosureId==other.Holding.DisclosureId?x with{SchemeVersion="other"}:x).ToArray(),[]);
            Check(mixed.Groups.Where(x=>x.Institution=="汇金资管").All(x=>x.SampleWeightPercent is null)&&mixed.Gaps.Any(x=>x.Code=="MixedClassificationStandards"),"不同分类标准版本不能拼接权重");
            var naming=EquityDirectionProjection.Build(Period,report.ObservedAt,repo.EquityWatchlist(),report.Positions.Select(x=>x.Holding).ToArray(),facts.Select(x=>x.DisclosureId==other.Holding.DisclosureId?x with{SectorCode="J",SectorName="合成矛盾名称"}:x).ToArray(),[]);
            Check(naming.Groups.Where(x=>x.Institution=="汇金资管").All(x=>x.SampleWeightPercent is null)&&naming.Gaps.Any(x=>x.Code=="ConflictingClassificationNames"),"同标准版本的相同行业代码不能有矛盾含义");
            Reject(()=>EquityDirectionProjection.Build(Period,report.ObservedAt,repo.EquityWatchlist(),report.Positions.Select(x=>x.Holding).Append(bank.Holding).ToArray(),facts,[]),"重复证券账户不放大样本权重");
            var future=EquityDirectionProjection.Build(Period,report.ObservedAt,repo.EquityWatchlist(),report.Positions.Select(x=>x.Holding).ToArray(),facts.Select(x=>x with{ReviewedAt=report.ObservedAt.AddDays(1)}).ToArray(),[]);
            Check(future.ClassifiedSecurities==0,"未来核验事实不穿越查询知识时点");
            var readRejected=false;try{repo.Read(r=>{r.ReviewEquityDirection(one with{PreviousReviewId=firstId.ToString("N")},Guid.NewGuid());return true;},default);}catch(InvalidOperationException){readRejected=true;}
            Check(readRejected,"只读快照不能写入方向核验");
            var cancelled=false;try{repo.Read(r=>r.EquityDirection(Period),new(true));}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"取消查询不返回空成功");
            var path=Path.Combine(rawPath,other.Holding.EvidenceHash+".raw");var bytes=File.ReadAllBytes(path);File.WriteAllText(path,"broken");
            var damageBlocked=false;
            try{var damaged=repo.Read(r=>r.EquityDirection(Period),default);damageBlocked=damaged.Gaps.Any(x=>x.Code=="EvidenceUnavailable")&&damaged.Groups.All(x=>x.SampleWeightPercent is null);}
            catch(InvalidDataException){damageBlocked=true;}
            Check(damageBlocked,"未巡检的原件损坏立即阻断查询或权重，不能缩小分母制造100%");
            File.WriteAllBytes(path,bytes);repo.ReconcileEvidenceIntegrity();
            var changed=two with{PreviousReviewId=secondId.ToString("N"),Style="原文明示合成风格"};repo.ReviewEquityDirection(changed,Guid.NewGuid());
            Reject(()=>repo.ReviewEquityDirection(two with{PreviousReviewId=secondId.ToString("N")},Guid.NewGuid()),"并发更正的旧基线不能覆盖新事实");
        }
        using(var restarted=TestMarketData.Open(data,raw))Check(restarted.EquityDirection(Period).Groups.Any(x=>x.Style=="原文明示合成风格"),"普通SQLite重启保留方向审核和当前版本");
        foreach(var (name,sql) in new[]{("missing-table","DROP TABLE equity_direction_review"),("unknown-version","UPDATE feature_schema_migration SET version=99 WHERE feature='equity-direction'"),("missing-migration","DELETE FROM feature_schema_migration WHERE feature='equity-direction'")})
        {
            var directory=Path.Combine(root,name);using(var initial=TestMarketData.Open(directory,new RawEvidenceStore(Path.Combine(root,name+"-raw")))){}
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"market.db"),Pooling=false}.ToString())){db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
            Reject(()=>{using var reopen=TestMarketData.Open(directory,new RawEvidenceStore(Path.Combine(root,name+"-raw")));},"库缺损不自动重建 "+name);
        }
        await Jobs(Path.Combine(root,"jobs"));await EquityDirectionModelChecks.RunAsync(JsonSerializer.Deserialize<EquityDirectionReport>(File.ReadAllText(Path.Combine(root,"snapshot.json")))!);
        Console.WriteLine("PASS 持仓方向专项完成；仅合成库、后台入口和界面模型，不代表正式服务或真实行业事实已核验。目录="+root);
    }
    private static EquityDirectionReview Review(EquityDirectionPosition position,string code,string name,decimal price)=>new(position.Holding.DisclosureId,position.Holding.EvidenceHash!,"合成行业标准","2026",code,name,null,"合成PDF P1 行业","synthetic-reviewer","隔离反例验证",true,price,Period,"合成PDF P2 A股期末价");
    private static void SeedSecond(MarketRepository repo)
    {
        var bytes=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(DirectEquityChecks.Source(new(2026,8,28),("中央汇金资产管理有限责任公司",9000,9))).Replace("600036","600153",StringComparison.Ordinal));
        repo.SaveEquityDisclosure("SSE","600153",OfficialEquitySource.Request("SSE","600153"),bytes);var disclosure=repo.EquityDisclosures(new("SSE","600153")).Single();
        repo.ReviewEquity(new(disclosure.Id,Period,new(2026,8,28),"https://www.sse.com.cn/fixture-direction.pdf","合成股东表","synthetic-reviewer",false,"Portfolio",Account:"中央汇金资产管理有限责任公司",OriginalConfirmed:true),"%PDF-synthetic-direction"u8.ToArray());
    }
    private static async Task Jobs(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using(var seed=TestMarketData.Open(Path.Combine(root,"data"),raw)){DirectEquityChecks.Seed(seed);}
        await using var session=new NationalTeamBackgroundSession(new Context(root),new NoNetwork());await session.StartAsync(default);
        Check(session.DescribeJobs().Single(x=>x.JobId=="equity-direction-review") is{EnabledByDefault:false,ManualOnly:true,RunOnStartup:false},"方向核验仅手动任务，不增加自动金融审核");
        var report=await Query(session);var review=Review(report.Positions.First(),"J","合成银行",10);var payload=JsonSerializer.Serialize(new EtfRequest("equity-direction-review",EquityDirectionReview:review));var command=Guid.NewGuid();
        Check((await session.ExecuteAsync("equity-direction-review",payload,default,command)).Status=="Succeeded","真实后台入口提交方向核验并持久化");
        Check((await Query(session)).Groups.All(x=>x.SampleWeightPercent==100m),"后台分块查询返回完整同报告期方向结果");
        Check((await session.ExecuteAsync("equity-direction-review",payload,default,command)).Status=="Succeeded","后台原命令重放幂等，无需再次联网");
        Check(session.QueryOperation(JsonSerializer.Serialize(new EtfRequest("equity-direction")))=="equity-direction"&&session.QueryOperation(payload)=="unknown","只读白名单允许方向读取，不能提交审核");
    }
    private static async Task<EquityDirectionReport> Query(NationalTeamBackgroundSession session)
    {
        var request=new EtfRequest("equity-direction",To:Period,Count:1);var blocks=new List<JsonDocumentBlock>();
        while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;request=request with{Offset=next,Snapshot=page.Snapshot};}
        return JsonDocumentBlocks.Decode<EquityDirectionReport>(blocks,default);
    }
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext{public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoNetwork:IPluginHttpGateway{public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("方向审核和查询禁止联网");}
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 持仓方向："+label);}
    private static void Reject(Action work,string label){try{work();}catch(InvalidDataException){Check(true,label);return;}throw new Exception("未拒绝："+label);}
}
