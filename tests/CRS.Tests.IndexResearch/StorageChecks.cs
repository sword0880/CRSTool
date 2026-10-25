using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>真实原件配合显式固定测试密钥及模拟时钟，只证明隔离存储，不接触正式Worker。</summary>
internal static class StorageChecks
{
    private sealed class Clock(DateTimeOffset at):TimeProvider{public DateTimeOffset At=at;public override DateTimeOffset GetUtcNow()=>At;}
    public static void Run(string sources,string output,DateTimeOffset at,CapturedIndexPacket broad,CapturedIndexPacket valuation,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.IndexStorage",Guid.NewGuid().ToString("N"));var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var clock=new Clock(at);var id=Guid.NewGuid();
        void Reject(Action action,string name){try{action();}catch(InvalidDataException){check(true,name);return;}throw new InvalidDataException("没有拒绝："+name);}
        CapturedIndexPacket Sector(string code)
        {
            var bytes=File.ReadAllBytes(Path.Combine(sources,code+"-4days.json"));var definition=IndexResearchCatalog.Get(code);
            return new("Price",code,new(2026,10,20),new(2026,10,23),[valuation.Documents.Single(x=>x.Role=="Method"),broad.Documents.Single(x=>x.Role=="Units"),new("Price",OfficialIndexSeriesSource.PriceSource(code,new(2026,10,20),new(2026,10,23)).AbsoluteUri,bytes,at.AddSeconds(-20),Convert.ToHexString(SHA256.HashData(bytes)))]);
        }
        CapturedIndexPacket Change(CapturedIndexPacket packet,string role,Action<JsonNode> change)
        {
            var source=packet.Documents.Single(x=>x.Role==role);var json=JsonNode.Parse(source.Content)!;change(json);var bytes=Encoding.UTF8.GetBytes(json.ToJsonString());
            return packet with{Documents=packet.Documents.Select(x=>x.Role==role?x with{Content=bytes,Sha256=Convert.ToHexString(SHA256.HashData(bytes))}:x).ToArray()};
        }
        using(var repository=TestMarketData.Open(data,raw,timeProvider:clock))
        {
            Reject(()=>repository.ImportIndexSeries(valuation,Guid.NewGuid()),"只有全局日期头而无同日价格不能登记PB");
            check(repository.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Count==0,"失败的估值导入不留下半批金融事实");
            var first=repository.ImportIndexSeries(broad,id);check(first.Added==261&&first.Pending==0,"261条真实宽基日线同批登记");
            check(repository.IndexSeriesRevisions(broad.From,broad.Through,clock.At.AddTicks(-1)).Count==0,"正式登记前截止不能看到历史回填");
            var replay=repository.ImportIndexSeries(broad,id);check(replay==first&&repository.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Count==261,"同命令重放不制造新事实或首见时间");
            var changed=Change(broad,"Price",x=>{x["data"]!["data"]![0]![1]=6173.037m;x["data"]!["data"]![0]![5]=6173.037m;});
            Reject(()=>repository.ImportIndexSeries(changed,id),"同命令不能换原件负载");
            check(repository.ImportIndexSeries(broad,Guid.NewGuid()) is {Added:0,Duplicates:261},"不同请求同原件只记复用，不复制修订");
            foreach(var definition in IndexResearchCatalog.Definitions.Values.Where(x=>x.Family=="Cni1000Sector"))repository.ImportIndexSeries(Sector(definition.Code),Guid.NewGuid());
            var imported=repository.ImportIndexSeries(valuation,Guid.NewGuid());check(imported.Added==10&&imported.Pending==0,"10个真实PB快照均与各自同日价格匹配");
            var snapshot=repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At);check(snapshot.Count(x=>x.Value.Kind=="Valuation"&&x.EvidenceHealthy)==10,"快照证明完整且原件健康");
            var axis=Enumerable.Range(0,4).Select(new DateOnly(2026,10,20).AddDays).ToDictionary(d=>d,_=>(bool?)true);
            var uiInputs=repository.IndexSeriesRevisions(new(2026,10,20),valuation.Through,clock.At);
            var uiReport=IndexSeriesResearch.Calculate(new(2026,10,20),valuation.Through,clock.At,uiInputs,axis,new string('A',64));
            check(uiReport.Rows.All(x=>x.AcceptedPriceDays==4&&x.Curve[0].NormalizedLevel==100),"界面回放必须传入完整4日而非仅统计日");
            File.WriteAllText(Path.Combine(output,"ui-replay-report.json"),System.Text.Json.JsonSerializer.Serialize(uiReport,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            var beforeCorrection=clock.At;clock.At=clock.At.AddMinutes(1);
            var corrected=Change(Sector("399381"),"Price",x=>{x["data"]!["data"]![0]![1]=3545.528m;x["data"]!["data"]![0]![5]=3545.528m;});
            check(repository.ImportIndexSeries(corrected,Guid.NewGuid()).Pending==1,"同日价格差异进入待审而非覆盖");
            var rows=repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At);check(!rows.Single(x=>x.Value.Code=="399381"&&x.Value.Kind=="Valuation").EvidenceHealthy,"价格差异待审立即阻断旧PB价格证明");
            check(repository.IndexSeriesRevisions(valuation.From,valuation.Through,beforeCorrection).Single(x=>x.Value.Code=="399381"&&x.Value.Kind=="Valuation").EvidenceHealthy,"价格新修订不能穿越旧截止否定当时有效快照");
            var candidate=rows.Single(x=>x.Value.Code=="399381"&&x.State=="NeedsReview");clock.At=clock.At.AddMinutes(1);repository.ReviewIndexSeries(candidate.Id,true,"隔离价格审核",Guid.NewGuid());
            check(!repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399381"&&x.Value.Kind=="Valuation").EvidenceHealthy,"价格基线更正后旧PB不自动继承新证明");
            Reject(()=>repository.ImportIndexSeries(valuation,Guid.NewGuid()),"旧PB收盘不匹配新价格不能重放恢复");
            var newValuation=Change(valuation,"Valuation",x=>x["data"]!["rows"]![1]!["closeingPoint"]=3545.528m);check(repository.ImportIndexSeries(newValuation,Guid.NewGuid()).Pending==1,"同日PB新证明需独立审核");
            var next=repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399381"&&x.Value.Kind=="Valuation"&&x.State=="NeedsReview");
            clock.At=clock.At.AddMinutes(1);repository.ReviewIndexSeries(next.Id,true,"隔离估值审核",Guid.NewGuid());
            check(repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399381"&&x.Value.Kind=="Valuation"&&x.State=="Accepted").EvidenceHealthy,"新PB审核绑定具体新价格修订");
            clock.At=clock.At.AddMinutes(1);repository.ImportIndexSeries(changed,Guid.NewGuid());
            var rejected=repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399317"&&x.State=="NeedsReview");
            clock.At=clock.At.AddMinutes(1);repository.ReviewIndexSeries(rejected.Id,false,"隔离拒绝后再送审",Guid.NewGuid());clock.At=clock.At.AddMinutes(1);
            check(repository.ImportIndexSeries(changed,Guid.NewGuid()).Pending==1&&repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399317"&&x.State=="NeedsReview").EvidenceHealthy,"旧捕获原件拒绝后可按新知识时点再送审，不能误判捕获时间不等");
            var actual=clock.At;clock.At=clock.At.AddSeconds(-1);Reject(()=>repository.ImportIndexSeries(broad,Guid.NewGuid()),"写时钟倒退不增加或倒填首见");clock.At=actual;
            var unitDoc=broad.Documents.Single(x=>x.Role=="Units");var path=Path.Combine(root,"raw",unitDoc.Sha256+".raw");var saved=File.ReadAllBytes(path);File.WriteAllText(path,"损坏");
            check(repository.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Where(x=>x.Value.Kind=="Price").All(x=>!x.EvidenceHealthy),"共同单位原件损坏阻断所有依赖价格");File.WriteAllBytes(path,saved);
            check(repository.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Where(x=>x.Value.Kind=="Price").All(x=>x.EvidenceHealthy),"恢复原哈希字节才解除原件阻断");
            using var corrupt=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Password=Convert.ToHexString(TestMarketData.Key),Pooling=false}.ToString());corrupt.Open();
            var tamperTarget=repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Value.Code=="399317"&&x.State=="NeedsReview");
            using(var tamper=corrupt.CreateCommand())
            {
                // 故障必须由独立连接提交后再读；未提交的SAVEPOINT对被测连接不可见。
                tamper.CommandText="UPDATE nt_index_revision SET values_json=$json WHERE id=$revision";tamper.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(tamperTarget.Value with{Close=tamperTarget.Value.Close+1}));tamper.Parameters.AddWithValue("$revision",tamperTarget.Id);tamper.ExecuteNonQuery();
                check(!repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At).Single(x=>x.Id==tamperTarget.Id).EvidenceHealthy,"仅金融值被改写、原件未变仍必须阻断证据");
                tamper.Parameters["$json"].Value=System.Text.Json.JsonSerializer.Serialize(tamperTarget.Value);tamper.ExecuteNonQuery();tamper.Parameters.Clear();
                tamper.CommandText="CREATE TEMP TABLE removed_index_events AS SELECT * FROM nt_index_event WHERE revision_id=$revision";tamper.Parameters.AddWithValue("$revision",tamperTarget.Id);tamper.ExecuteNonQuery();
                tamper.CommandText="DELETE FROM nt_index_event WHERE revision_id=$revision";tamper.ExecuteNonQuery();
                Reject(()=>repository.IndexSeriesRevisions(valuation.From,valuation.Through,clock.At),"缺状态记录不能静默丢失待审事实并恢复旧接受值");
                tamper.CommandText="INSERT INTO nt_index_event SELECT * FROM removed_index_events";tamper.ExecuteNonQuery();tamper.CommandText="DROP TABLE removed_index_events";tamper.ExecuteNonQuery();
            }
            using var command=corrupt.CreateCommand();command.CommandText="DELETE FROM nt_index_command WHERE id=$id";command.Parameters.AddWithValue("$id",id.ToString("N"));command.ExecuteNonQuery();Reject(()=>repository.IndexSeriesReceipt(id),"金融回执缺损不能被全局回执冒充完整");
            var reverse=Guid.NewGuid();repository.ImportIndexSeries(broad,reverse);command.CommandText="DELETE FROM worker_business_command WHERE request=$id";command.Parameters["$id"].Value=reverse.ToString("N");command.ExecuteNonQuery();Reject(()=>repository.IndexSeriesReceipt(reverse),"全局回执缺损不能被金融回执冒充完整");
        }
        using(var reopened=TestMarketData.Open(data,raw,timeProvider:clock))check(reopened.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Count==315,"加密库独立重启保留301价格、10快照与四项修订");
        using(var corrupt=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Password=Convert.ToHexString(TestMarketData.Key),Pooling=false}.ToString()))
        {corrupt.Open();using var command=corrupt.CreateCommand();command.CommandText="DROP TABLE nt_index_event";command.ExecuteNonQuery();}
        Reject(()=>{using var broken=TestMarketData.Open(data,raw,timeProvider:clock);},"已初始化指数库缺表不能自动重建为无数据");
        using var cancel=new CancellationTokenSource();var armed=false;var cancelData=Path.Combine(root,"cancel");
        using(var repo=TestMarketData.Open(cancelData,new RawEvidenceStore(Path.Combine(root,"cancel-raw")),()=>{if(armed)cancel.Cancel();},clock))
        {
            armed=true;try{repo.ExecuteOperation(()=>repo.ImportIndexSeries(broad,Guid.NewGuid()),cancel.Token);throw new Exception("未取消");}catch(OperationCanceledException){check(true,"写边界取消能停止真实事务");}
            check(repo.IndexSeriesRevisions(broad.From,broad.Through,clock.At).Count==0,"取消后不留下半批指数事实");
        }
        File.WriteAllText(Path.Combine(output,"storage-boundary.txt"),"IsolatedSqlCipherWithSyntheticClockAndExplicitFixtureKey; not installed acceptance.");
    }
}
