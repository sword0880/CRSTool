using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

if(args is not ["--source",var sourceDirectory,"--output",var output])throw new ArgumentException("需要明确公开原件目录及隔离输出目录。");
Directory.CreateDirectory(output);var passed=0;var now=DateTimeOffset.UtcNow;var from=new DateOnly(2025,9,19);var through=new DateOnly(2026,10,23);
void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 指数研究："+name);}
void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new InvalidDataException("没有拒绝："+name);}
CapturedIndexDocument Doc(string role,string url,string name,int seconds)=>new(role,url,File.ReadAllBytes(Path.Combine(sourceDirectory,name)),now.AddSeconds(seconds),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sourceDirectory,name)))));
CapturedIndexDocument Change(CapturedIndexDocument document,Action<JsonNode> change)
{var node=JsonNode.Parse(document.Content)!;change(node);var bytes=Encoding.UTF8.GetBytes(node.ToJsonString());return document with{Content=bytes,Sha256=Convert.ToHexString(SHA256.HashData(bytes))};}
var broad=IndexResearchCatalog.Get("399317");var methodA=Doc("Method",broad.MethodUrl,"method-a.pdf",-40);var methodSector=Doc("Method",IndexResearchCatalog.Get("399381").MethodUrl,"method-sector.pdf",-40);
var units=Doc("Units",OfficialIndexSeriesSource.UnitsSource.AbsoluteUri,"index-detail.html",-30);
var price=Doc("Price",OfficialIndexSeriesSource.PriceSource(broad.Code,from,through).AbsoluteUri,"cni-a-20250919-20261023.json",-20);
var packet=new CapturedIndexPacket("Price",broad.Code,from,through,[methodA,units,price]);var values=OfficialIndexSeriesSource.Parse(packet,now);
Check(values.Count==261&&values[0].Date==from&&values[^1].Date==through,"官方宽基原件261日、范围及数量一致");
Check(values[^1].Close==6172.0370m,"收盘点位使用close字段且不误用开盘或最高");
Check(values[^1].SampleAmountYuan==1910097000000m&&values[^1].SampleVolumeShares==121593840000m,"官网亿元/万手按decimal归一为元/股，仍是指数样本成交");
Reject(()=>OfficialIndexSeriesSource.PriceSource("600036",from,through),"任意股票不能当作已核指数采集");
Check(!OfficialIndexSeriesSource.IsSource(new(price.Url+"&other=1"))&&!OfficialIndexSeriesSource.IsSource(new(price.Url.Replace("hq.cnindex.com.cn","other.cnindex.com.cn"))),"授权地址不扩大为额外参数或任意子域名");
Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units,price with{Sha256=new string('0',64)}]},now),"原件摘要不符拒绝");
Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodSector,units,price]},now),"不能混用行业与宽基方法");
Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units with{Content="单位未知"u8.ToArray(),Sha256=Convert.ToHexString(SHA256.HashData("单位未知"u8))},price]},now),"未核验单位和样本范围不能接收成交数值");
var badCount=Change(price,x=>x["total"]=260);Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units,badCount]},now),"数组数量与官网total不同不能作为完整原件");
var duplicateDate=Change(price,x=>x["data"]!["data"]![1]![0]=through.ToString("yyyy-MM-dd"));Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units,duplicateDate]},now),"重复交易日拒绝");
var badClose=Change(price,x=>x["data"]!["data"]![0]![5]=1);Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units,badClose]},now),"OHLC或收盘身份冲突拒绝");
var badColumns=Change(price,x=>x["data"]!["item"]![5]="nav");Reject(()=>OfficialIndexSeriesSource.Parse(packet with{Documents=[methodA,units,badColumns]},now),"字段表不能把净值或不同数值混作指数");
var oldCulture=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ar-SA");Check(OfficialIndexSeriesSource.PriceSource("399317",from,through)==new Uri(price.Url)&&OfficialIndexSeriesSource.Parse(packet,now).SequenceEqual(values),"地区设置不改变公历地址或数值解析");}finally{CultureInfo.CurrentCulture=oldCulture;}
var header=Doc("HeaderBefore",OfficialIndexSeriesSource.DaySource.AbsoluteUri,"query-day.json",-18);var list=Doc("Valuation",OfficialIndexSeriesSource.ValuationSource.AbsoluteUri,"industry-index-list-1.json",-16);var after=header with{Role="HeaderAfter",RetrievedAt=now.AddSeconds(-14)};
var snapshot=new CapturedIndexPacket("Valuation",null,through,through,[methodSector,header,list,after]);var estimates=OfficialIndexSeriesSource.Parse(snapshot,now);
Check(estimates.Count==10&&estimates.Single(x=>x.Code=="399381") is {PublishedPb:1.42m,PublishedRollingPe:15.1588m,Close:3544.528m},"真实目录10指数与公布PB/滚动PE一致");
Check(estimates.All(x=>x.ReportedSamples==0),"原件样本数占位0保持来源事实，不推导覆盖率");
Reject(()=>OfficialIndexSeriesSource.Parse(snapshot with{From=through.AddDays(-1),Through=through.AddDays(-1)},now),"当期估值不能伪装为历史日期");
Reject(()=>OfficialIndexSeriesSource.Parse(snapshot with{Documents=[methodSector,header,list,after with{RetrievedAt=header.RetrievedAt.AddSeconds(-1)}]},now),"前后日期头的取得顺序不能倒置");
var missingCode=Change(list,x=>x["data"]!["rows"]![0]!["indexcode"]="999999");Reject(()=>OfficialIndexSeriesSource.Parse(snapshot with{Documents=[methodSector,header,missingCode,after]},now),"固定目录缺一个指数时不能部分批准快照");
var negativePb=Change(list,x=>x["data"]!["rows"]![1]!["pb"]=-1);Check(OfficialIndexSeriesSource.Parse(snapshot with{Documents=[methodSector,header,negativePb,after]},now).Single(x=>x.Code=="399381").PublishedPb==-1,"非正PB保留原始事实而不伪造正估值");

// 日历来自原件日期的显式隔离夹具；不能把这里的周末补法当作正式交易日历验收。
var days=values.Select(x=>x.Date).ToHashSet();var calendar=Enumerable.Range(0,through.DayNumber-from.DayNumber+1).Select(from.AddDays).ToDictionary(d=>d,d=>(bool?)days.Contains(d));var calendarHash=new string('A',64);
IndexSeriesRevision Revision(IndexSeriesValue value,string state="Accepted",string? id=null)=>new(id??Guid.NewGuid().ToString("N"),value,1,state,now.AddSeconds(-10),now.AddSeconds(-8),new string('B',64),true,IndexResearchCatalog.Get(value.Code).MethodHash);
var revisions=values.Select(x=>Revision(x)).ToArray();var report=IndexSeriesResearch.Calculate(from,through,now,revisions,calendar,calendarHash);var row=report.Rows.Single(x=>x.Code=="399317");
Check(row.AcceptedPriceDays==261&&row.Curve[0].NormalizedLevel==100&&row.Drawdown250.HasValue,"完整日历夹具的共同起点及250日价格指标可复算");
Check(report.Rows.Single(x=>x.Code=="399381").Curve.All(x=>x.NormalizedLevel is null),"缺起点指数不能独选自己的首日归一");
var gap=revisions.Where(x=>x.Value.Date!=values[1].Date).ToArray();Check(IndexSeriesResearch.Calculate(from,through,now,gap,calendar,calendarHash).Rows.Single(x=>x.Code=="399317").Curve[1].NormalizedLevel is null,"缺日断线而不延续或补零");
var conflict=revisions.Append(Revision(values[^1] with{Close=values[^1].Close+1},"NeedsReview") with{EvidenceHealthy=false}).ToArray();Check(IndexSeriesResearch.Calculate(from,through,now,conflict,calendar,calendarHash).Rows.Single(x=>x.Code=="399317").LatestClose is null,"不健康待审修订仍阻断同日旧接受版本");
var future=revisions.Select(x=>x with{FirstSeenAt=now.AddDays(1),StateRecordedAt=now.AddDays(1)}).ToArray();Check(IndexSeriesResearch.Calculate(from,through,now,future,calendar,calendarHash).Rows.All(x=>x.AcceptedPriceDays==0),"知识截止后登记的历史回填不参与计算");
Check(IndexSeriesResearch.Calculate(from,through,now,revisions,calendar,"").Rows.All(x=>x.Drawdown250 is null),"缺日历原件身份不能给出250日指标");
Check(IndexSeriesResearch.Calculate(from,through,now,revisions.Reverse().ToArray(),calendar,calendarHash).Fingerprint==report.Fingerprint,"输入顺序不改变结构化报告身份");
Check(IndexSeriesResearch.Calculate(from,through,now.ToOffset(TimeSpan.FromHours(8)),revisions,calendar,calendarHash).Fingerprint==report.Fingerprint,"同一截止时刻不同显示时区具有相同身份");
Check(report.Rows.All(x=>x.OpportunityState.StartsWith("NotEvaluated",StringComparison.Ordinal))&&report.TimingProof=="NotProven"&&report.InstitutionalTradingEvidence=="NotObserved","价格齐全不冒充低估机会、模型验证或机构交易");
File.WriteAllText(Path.Combine(output,"official-price-replay.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
StorageChecks.Run(sourceDirectory,output,now,packet,snapshot,Check);
await SessionChecks.Run(sourceDirectory,Check);
File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Calendar="ExplicitSyntheticFixture",Boundary="CapturedOfficialFilesWithSyntheticAcquisitionTimesAndCalendar"},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS 指数研究合计 {passed}；公开原件隔离解析/领域，非正式运行验收。");
