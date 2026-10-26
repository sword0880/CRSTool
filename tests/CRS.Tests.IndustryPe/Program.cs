using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

if(args is ["--official",var officialFile,var officialOutput]){OfficialSample.Run(officialFile,officialOutput);return;}

var output=args is ["--output",var folder]?folder:Path.Combine("artifacts","industry-pe-checks-20261025");
Directory.CreateDirectory(output);var root=Path.Combine(output,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var date=new DateOnly(2026,10,23);var clock=new TestClock();var key=RandomNumberGenerator.GetBytes(32);var rawPath=Path.Combine(root,"raw");var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(rawPath);var passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS 行业PE："+name);}
void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("未拒绝："+name);}
CapturedIndustryPeDocument Capture(byte[] bytes)=>new(OfficialIndustryPeSource.Source(date).AbsoluteUri,date,bytes,clock.Start.AddMinutes(-1),Convert.ToHexString(SHA256.HashData(bytes)));
var source=Capture(Fixture(date));
CapturedIndustryPeDocument Change(Action<JsonArray> edit)
{var json=JsonNode.Parse(source.Content)!.AsArray();edit(json);return Capture(JsonSerializer.SerializeToUtf8Bytes(json));}
JsonArray Items(JsonArray json)=>json[0]!["sylItemVoList"]!.AsArray();
var parsed=OfficialIndustryPeSource.Parse(source,clock.At);
Check(parsed.Count==11&&parsed[0].RollingWeighted==10&&parsed[0].RollingMedian==11&&parsed[0].StaticWeighted==12&&parsed[0].StaticMedian==13,"滚动/静态及加权/中位数字段按官网表头分别解析");
Reject(()=>OfficialIndustryPeSource.Parse(source with{Sha256=new('a',64)},clock.At),"原件哈希不符拒绝");
Reject(()=>OfficialIndustryPeSource.Parse(source with{RetrievedAt=clock.At.AddTicks(1)},clock.At),"取得时刻不能在知识时间之后");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[0]!["date"]="2026-10-22"),clock.At),"请求日期与正文日期冲突拒绝");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>j[0]!["plate"]="深圳市场"),clock.At),"市场范围不能用深市数据冒充全市场");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j).RemoveAt(0)),clock.At),"一级行业不完整不能部分入库");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[1]!["tc"]="C01"),clock.At),"一级行业重复拒绝");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[0]!["tt"]="wrong"),clock.At),"分类来源不能混接");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[0]!["dwer"]="0"),clock.At),"零PE不解释为低估");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[0]!["dwer"]="-1"),clock.At),"负PE拒绝");
Reject(()=>OfficialIndustryPeSource.Parse(Change(j=>Items(j)[0]!["ca"]="0"),clock.At),"零公司不能有有效PE");
var missing=Change(j=>{foreach(var name in new[]{"dwer","dmer","swer","smer"})Items(j)[0]![name]="NA";Items(j)[0]!["ca"]="0";});
Check(OfficialIndustryPeSource.Parse(missing,clock.At)[0].RollingWeighted is null,"NA保留未知，不补零");
var duplicateJson=Encoding.UTF8.GetString(source.Content).Replace("\"dwer\":\"10\"","\"dwer\":\"10\",\"dwer\":\"9\"",StringComparison.Ordinal);
Reject(()=>OfficialIndustryPeSource.Parse(Capture(Encoding.UTF8.GetBytes(duplicateJson)),clock.At),"重复JSON金融字段拒绝");
Check(new[]{"http://www.cnindex.com.cn/syl/2026-10-23/cninfo.json","https://www.cnindex.com.cn/syl/2026-10-23/cninfo.json?x=1","https://www.cnindex.com.cn/syl/2026-10-23/cninfo.json#x","https://evil.cnindex.com.cn/syl/2026-10-23/cninfo.json","https://www.cnindex.com.cn/syl/2026-10-23/crsc.json"}.All(x=>!OfficialIndustryPeSource.IsSource(new(x))),"来源白名单不扩到其它协议、域名、分类或任意参数");
var oldCulture=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("th-TH");Check(OfficialIndustryPeSource.Source(date).AbsolutePath=="/syl/2026-10-23/cninfo.json","非公历地区设置不能改变官网日期路径");}finally{CultureInfo.CurrentCulture=oldCulture;}

var command=Guid.NewGuid();DateTimeOffset initialAt;string acceptedId;
using(var repository=new MarketRepository(data,raw,approvalAuditKey:key,timeProvider:clock))
{
    Check(repository.IndustryPeRevisions(date,date,clock.At).Count==0,"空库没有假行业事实");
    var initial=repository.ImportIndustryPe(source,command);initialAt=clock.At;acceptedId=repository.IndustryPeRevisions(date,date,clock.At)[0].Id;
    Check(initial is {Added:11,Pending:0},"一个原件的11个行业单事务登记");
    Check(repository.IndustryPeRevisions(date,date,clock.At).All(x=>x.FirstSeenAt==initialAt&&x.EvidenceHealthy),"首见为实际登记，全部原件健康");
    Check(repository.IndustryPeRevisions(date,date,initialAt.AddTicks(-1)).Count==0,"登记前知识时点看不到回填历史");
    clock.Next();Check(repository.ImportIndustryPe(source,command)==initial,"同命令重放返回原回执");
    Check(repository.ImportIndustryPe(source,Guid.NewGuid()) is {Added:0,Duplicates:11},"相同原件另一命令不重复建事实");
    var corrected=Change(j=>Items(j)[0]!["dwer"]="14");
    Reject(()=>repository.ImportIndustryPe(corrected,command),"同请求身份不能改金融负载");
    clock.Next();var conflict=repository.ImportIndustryPe(corrected,Guid.NewGuid());var conflictAt=clock.At;
    var versions=repository.IndustryPeRevisions(date,date,clock.At);var candidate=versions.Single(x=>x.State=="NeedsReview");
    Check(conflict is {Added:1,Pending:1,Duplicates:10},"同日修正只追加差异版本并待审");
    var calendar=new Dictionary<DateOnly,bool?>{{date,true}};
    var blocked=IndustryPeResearch.Calculate(date,date,clock.At,versions,calendar,new('b',64));
    Check(blocked.Rows[0].LatestRollingWeighted is null&&blocked.Rows[0].State=="NeedsReview","待审冲突阻断该行业日期，不择优旧值");
    Check(repository.IndustryPeRevisions(date,date,initialAt).Count==11,"冲突登记前的知识版本仍可重放");
    clock.Next();var review=Guid.NewGuid();repository.ReviewIndustryPe(candidate.Id,true,"fixture-reviewer",review);
    var reviewed=repository.IndustryPeRevisions(date,date,clock.At);
    Check(reviewed.Single(x=>x.Id==candidate.Id).State=="Accepted"&&reviewed.Single(x=>x.Id==acceptedId).State=="Superseded","审核在同一事务替换接受版本");
    Check(repository.IndustryPeRevisions(date,date,conflictAt).Single(x=>x.Id==candidate.Id).State=="NeedsReview","审核不能改写较早知识状态");
    repository.ReviewIndustryPe(candidate.Id,true,"fixture-reviewer",review);Check(true,"审核同命令稳定重放");
    Reject(()=>repository.ReviewIndustryPe(candidate.Id,false,"fixture-reviewer",review),"同审核命令不能改变决定");
    var watermark=clock.At;clock.At=clock.At.AddTicks(-1);
    Reject(()=>repository.ImportIndustryPe(source,Guid.NewGuid()),"时钟倒退停止写入");clock.At=watermark;clock.Next();
    Check(((IMarketReadSessions)repository).Read(r=>r.IndustryPeRevisions(date,date,clock.At),default).Count==12,"独立只读连接可回读行业修订");
    File.WriteAllBytes(Path.Combine(rawPath,source.Sha256+".raw"),[1,2,3]);
    var damaged=repository.IndustryPeRevisions(date,date,clock.At);
    Check(damaged.Any(x=>!x.EvidenceHealthy),"原件即时损坏被读取发现");
    Reject(()=>IndustryPeResearch.Calculate(date,date,clock.At,damaged,calendar,new('b',64)),"原件损坏不返回半份可信研究");
    File.WriteAllBytes(Path.Combine(rawPath,source.Sha256+".raw"),source.Content);
    Check(repository.IndustryPeRevisions(date,date,clock.At).All(x=>x.EvidenceHealthy),"未产生持久Q15事件的隔离字节恢复后重新核验");
    var calendarSnapshot=repository.IndustryPeCalendar(date,date,clock.At);
    Check(calendarSnapshot.Days[date] is null,"未登记日历不能用星期推算交易日");
    using var corrupt=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());corrupt.Open();
    using var delete=corrupt.CreateCommand();delete.CommandText="DELETE FROM nt_industry_command WHERE id=$id";delete.Parameters.AddWithValue("$id",command.ToString("N"));delete.ExecuteNonQuery();
    Reject(()=>repository.IndustryPeReceipt(command),"业务命令存在而金融回执缺失时失败关闭");
    var reverse=Guid.NewGuid();clock.Next();repository.ImportIndustryPe(source,reverse);
    delete.Parameters["$id"].Value=reverse.ToString("N");delete.CommandText="DELETE FROM worker_business_command WHERE request=$id";delete.ExecuteNonQuery();
    Reject(()=>repository.IndustryPeReceipt(reverse),"金融回执存在而业务命令缺失时也失败关闭");
}
using(var reopened=new MarketRepository(data,raw,approvalAuditKey:key,timeProvider:clock))Check(reopened.IndustryPeRevisions(date,date,clock.At).Count==12,"加密库重启仍保留全部修订和审核");
using(var corruptedSchema=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString()))
{corruptedSchema.Open();using var drop=corruptedSchema.CreateCommand();drop.CommandText="DROP TABLE nt_industry_event";drop.ExecuteNonQuery();}
Reject(()=>{using var missingTable=new MarketRepository(data,raw,approvalAuditKey:key,timeProvider:clock);},"已初始化库缺表不能自动重建成无数据状态");

// 取消在写入边界触发，验证真实事务而非只验证入口的预取消令牌。
var cancelRoot=Path.Combine(root,"cancel");using var cancellation=new CancellationTokenSource();var armed=false;
using(var repository=new MarketRepository(cancelRoot,new RawEvidenceStore(Path.Combine(root,"cancel-raw")),()=>{if(armed)cancellation.Cancel();},key,clock))
{
    armed=true;
    try{repository.ExecuteOperation(()=>repository.ImportIndustryPe(source,Guid.NewGuid()),cancellation.Token);throw new Exception("未取消");}catch(OperationCanceledException){Check(true,"写边界取消会停止事务");}
    Check(repository.IndustryPeRevisions(date,date,clock.At).Count==0,"取消后没有半天行业事实");
}

// 纯领域反例使用明确合成的日期轴；不声称它是交易所日历或真实低估样本。
var begin=new DateOnly(2025,10,1);var end=begin.AddDays(260);var syntheticCalendar=Enumerable.Range(0,261).ToDictionary(begin.AddDays,_=>(bool?)true);
var synthetic=Enumerable.Range(0,261).Select(i=>new IndustryPeRevision("r"+i,begin.AddDays(i),new("C01","能源",100,i==260?5:20,21,22,23),1,"Accepted",clock.Start,clock.Start,"capture"+i,true)).ToArray();
var report=IndustryPeResearch.Calculate(begin,end,clock.At,synthetic,syntheticCalendar,new('a',64));
Check(report.Rows[0].PublishedSeriesPercentile==0&&report.Rows[0].PriorSamples==260,"区间分位只比较本行业且排除当日");
Check(report.Rows[0].PublishedMedianSeriesPercentile==50m&&report.Rows[0].MedianPriorSamples==260
    &&IndustryPePosition.Classify(report.Rows[0])==IndustryPeObservation.SingleMetricLow,"加权PE低位但中位PE不低，只能是单口径低位");
IndustryPeReport WithLastMedian(decimal? value)=>IndustryPeResearch.Calculate(begin,end,clock.At,
    synthetic.Select(x=>x.Date==end?x with{Values=x.Values with{RollingMedian=value}}:x).ToArray(),syntheticCalendar,new('a',64));
var dual=WithLastMedian(5m).Rows[0];
Check(dual.PublishedMedianSeriesPercentile==0m&&IndustryPePosition.Classify(dual)==IndustryPeObservation.DualHistoricalLow,"两个独立历史分位都低才列研究观察");
Check(IndustryPePosition.Classify(WithLastMedian(40m).Rows[0])==IndustryPeObservation.MetricDivergence,"加权与中位PE强烈分歧应显式预警");
Check(IndustryPePosition.Classify(dual with{PublishedMedianSeriesPercentile=null})==IndustryPeObservation.EvidenceIncomplete,"旧后台没有中位分位不能自动视作双口径低位");
Check(IndustryPePosition.Classify(dual with{MedianPriorSamples=251})==IndustryPeObservation.EvidenceIncomplete,"即使分位为低，缺少252中位样本也须拒绝");
Check(IndustryPePosition.Classify(dual with{ExpectedPriorDays=350})==IndustryPeObservation.EvidenceIncomplete,"有效样本低于核验日历80%时拒绝");
Check(IndustryPePosition.Classify(dual with{ExpectedPriorDays=200})==IndustryPeObservation.EvidenceIncomplete,"不允许有效样本超过历史交易日分母");
Check(WithLastMedian(0m).Rows[0].PublishedMedianSeriesPercentile is null,"零和非正中位PE不形成低估信号");
var insufficientMedian=synthetic.Select((x,i)=>i<40?x with{Values=x.Values with{RollingMedian=0m}}:x).ToArray();
var partial=IndustryPeResearch.Calculate(begin,end,clock.At,insufficientMedian,syntheticCalendar,new('a',64)).Rows[0];
Check(partial.PublishedSeriesPercentile==0m&&partial.PublishedMedianSeriesPercentile is null
    &&IndustryPePosition.Classify(partial)==IndustryPeObservation.EvidenceIncomplete,"中位PE前置正值不足不能借用加权PE覆盖率");
var zeroCurrent=synthetic.Select(x=>x.Date==end?x with{Values=x.Values with{RollingWeighted=0m}}:x).ToArray();
Check(IndustryPeResearch.Calculate(begin,end,clock.At,zeroCurrent,syntheticCalendar,new('a',64)).Rows[0].PublishedSeriesPercentile is null,"零PE不排序为最低历史分位");

Check(report.OpportunityState=="NotEvaluatedMissingCoveragePbFundamentals"&&report.TimingProof=="NotProven","低公布PE分位不能升级为低估机会");
Check(IndustryPeResearch.Calculate(begin,end,clock.At,synthetic.TakeLast(252).ToArray(),syntheticCalendar,new('a',64)).Rows[0].PublishedSeriesPercentile is null,"历史不足252个前置样本保留未知");
Check(IndustryPeResearch.Calculate(begin,end,clock.At,synthetic,syntheticCalendar,new('b',64)).Fingerprint!=report.Fingerprint,"日历证据换版会改变指纹");
Check(IndustryPeResearch.Calculate(begin,end,clock.At,synthetic.Reverse().ToArray(),syntheticCalendar,new('a',64)).Fingerprint==report.Fingerprint,"输入顺序不改变研究身份");
Check(IndustryPeResearch.Calculate(begin,end,clock.At.ToOffset(TimeSpan.FromHours(8)),synthetic.Select(x=>x with{FirstSeenAt=x.FirstSeenAt.ToOffset(TimeSpan.FromHours(8)),StateRecordedAt=x.StateRecordedAt.ToOffset(TimeSpan.FromHours(8))}).ToArray(),syntheticCalendar,new('a',64)).Fingerprint==report.Fingerprint,"管道展示时区不改变同一行业研究身份");
syntheticCalendar[begin]=null;Check(!IndustryPeResearch.Calculate(begin,end,clock.At,synthetic,syntheticCalendar,new('a',64)).CalendarVerified,"缺日历不产生历史分母");
await IndustrySourceBudgetChecks.RunAsync(root,Check);
// CI 专用的11行业合成单日报告；绝不是实际官网PE或投资低估结论。
var uiFixture=IndustryPeResearch.Calculate(date,date,clock.At,
    parsed.Select((x,i)=>new IndustryPeRevision("synthetic-ui-"+i,date,x,1,"Accepted",clock.Start,clock.Start,"synthetic-ui",true)).ToArray(),
    new Dictionary<DateOnly,bool?>{{date,true}},new('a',64));
File.WriteAllText(Path.Combine(output,"synthetic-industry-ui-report.json"),JsonSerializer.Serialize(uiFixture));
File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticAndIsolatedEncryptedStorage",Root=root},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS 行业PE合计 {passed}；隔离解析/事务/领域，非正式运行或机会验证。");

static byte[] Fixture(DateOnly day)
{
    var rows=new JsonArray();foreach(var sector in IndustryPeResearch.Sectors)rows.Add(new JsonObject{["date"]=day.ToString("yyyy-MM-dd"),["tc"]=sector.Key,["tn"]=sector.Value,["tl"]="1",["tt"]="008200",["plate"]="深沪京市场",["ca"]="100",["dwer"]="10",["dmer"]="11",["swer"]="12",["smer"]="13"});
    return JsonSerializer.SerializeToUtf8Bytes(new JsonArray(new JsonObject{["id"]=4,["plate"]="深沪京市场",["sylItemVoList"]=rows}));
}
sealed class TestClock:TimeProvider
{
    public DateTimeOffset Start {get;}=DateTimeOffset.UtcNow;public DateTimeOffset At=DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow()=>At;public void Next()=>At=At.AddSeconds(1);
}
