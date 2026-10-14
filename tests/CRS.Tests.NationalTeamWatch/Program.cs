using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
static void Check(bool condition,string name) {if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
static void Reject(Action action,string name) {try{action();}catch(Exception){Console.WriteLine("PASS "+name);return;}throw new Exception(name);}
Check(EtfMetrics.Normalize(2489718.77m,"万份")==24897187700m,"DATA-01 decimal units");
Reject(()=>EtfMetrics.Normalize(1,"亿元"),"Unknown unit rejected");
var dates=Enumerable.Range(0,25).Select(x=>new DateOnly(2026,9,1).AddDays(x)).ToArray();
var points=dates.Select((date,i)=>new SharePoint(date,100+i,2+i/100m,i.ToString())).ToList();
Check(EtfMetrics.Calculate(dates[5],5,dates,points).EstimatedFlow==10.15m,"Daily amounts summed over window");
Check(EtfMetrics.Calculate(dates[5],1,dates,points.Where(x=>x.Date!=dates[4]).ToList()).State=="Incomplete","Adjacent day gap never skipped");
var missingNav=points.Select(x=>x.Date==dates[5]?x with{Nav=null}:x).ToArray();
Check(EtfMetrics.Calculate(dates[5],1,dates,missingNav) is {DeltaShares:1,EstimatedFlow:null},"Missing NAV preserves shares only");
Check(EtfMetrics.Calculate(dates[20],20,dates,points).Fingerprint==EtfMetrics.Calculate(dates[20],20,dates,points).Fingerprint,"Reproducible input fingerprint");
Reject(()=>ImportFormat.Shares("market,ticker,trade_date,total_shares,unit\nSSE,510300,2026-09-01,2,亿元"),"CSV unknown unit rejected");
Reject(()=>ImportFormat.Shares("market,ticker,trade_date,total_shares,unit\nSSE,510300,2026-09-01,2,份\nSSE,510300,2026-09-01,2,份"),"Duplicate file rows rejected");
var root=Path.Combine(Path.GetTempPath(),"CRS-ETF-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
using(var repository=new MarketRepository(Path.Combine(root,"data"),raw))
{
 string Fetch(string body)=>repository.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(body)),"https://www.sse.com.cn/market/funddata/volumn/etfvolumn/");
 repository.ImportCalendar(Fetch("calendar evidence"),dates.Select(x=>("SSE",x)).ToList(),CancellationToken.None);
 var rows=new List<ImportRow>{new(new("SSE","510300"),dates[0],100,"份"),new(new("SSE","510300"),dates[1],110,"份")};
 repository.ImportShares(Fetch("shares evidence"),rows,CancellationToken.None);
 var original=repository.Evidence().Count;repository.ImportShares(Fetch("shares evidence"),rows,CancellationToken.None);
 Check(repository.Evidence().Count==original,"DATA-02 duplicate attempts do not duplicate facts");
 repository.ImportShares(Fetch("revised evidence"),[new(new("SSE","510300"),dates[1],115,"份")],CancellationToken.None);
 Check(repository.Points(new("SSE","510300")).Count==1,"Pending conflict blocks affected metrics");
 var candidate=repository.Evidence().Single(x=>x.State=="NeedsReview");
 Reject(()=>repository.Review(candidate.RevisionId,true,"test-reviewer"),"Corporate-action comparability confirmation required in backend");
 repository.Review(candidate.RevisionId,true,"test-reviewer",true);
 Check(repository.Points(new("SSE","510300")).Last().Shares==115,"Accepted revision replaces current pointer");
 Check(repository.Evidence().Count==3,"Old revisions retained");
 repository.ImportShares(Fetch("jump evidence"),[new(new("SSE","510300"),dates[2],230,"份")],CancellationToken.None);
 Check(repository.Evidence().Count(x=>x.State=="NeedsReview")==1,"Suspicious doubling is review, not zero flow");
 for(var i=0;i<15;i++)repository.SetMembership(new("SSE",(600000+i).ToString()),"pilot","index",true,"test");
 Reject(()=>repository.SetMembership(new("SSE","600020"),"extra","index",true,"test"),"Database pool capacity 20 enforced");
 Check(repository.Overview().Count==20,"Pool remains 20 after failed transaction");
 repository.Snapshot(Path.Combine(root,"snapshot.db"));Check(File.Exists(Path.Combine(root,"snapshot.db")),"WAL-safe SQLite backup snapshot");
 File.WriteAllText(Path.Combine(root,"raw",candidate.Hash+".raw"),"tampered");
 Check(repository.Points(new("SSE","510300")).Count==1,"DATA-09 tampered raw data excluded");
 Reject(()=>repository.ImportShares(Fetch("unknown date"),[new(new("SSE","510300"),new(2026,8,1),1,"份")],CancellationToken.None),"Unreviewed calendar dates rejected");
}
Console.WriteLine("ETF deterministic/offline integration checks passed.");
// 使用分日历证据的合成数据复现“中间日损坏后被跳过”的问题；这些日期不是实际交易所日历。
var qualityRoot=Path.Combine(root,"coverage");var qualityRaw=new RawEvidenceStore(Path.Combine(qualityRoot,"raw"));
using(var repository=new MarketRepository(Path.Combine(qualityRoot,"data"),qualityRaw))
{
 string Fetch(string text)=>repository.RecordFetch(qualityRaw.Save(Encoding.UTF8.GetBytes(text)),"https://www.sse.com.cn/market/funddata/volumn/etfvolumn/");
 var key=new EtfKey("SSE","510300");var four=dates.Take(4).ToArray();
 var middleHash=qualityRaw.Save(Encoding.UTF8.GetBytes("middle calendar original"));
 repository.ImportCalendar(repository.RecordFetch(middleHash,"https://www.sse.com.cn/"),[("SSE",four[1])],CancellationToken.None);
 repository.ImportCalendar(Fetch("other calendar original"),four.Where(x=>x!=four[1]).Select(x=>("SSE",x)).ToList(),CancellationToken.None);
 repository.ImportShares(Fetch("shares fixture"),[new(key,four[0],100,"份"),new(key,four[1],105,"份"),new(key,four[2],110,"份")],CancellationToken.None);
 var coverage=repository.Coverage(key,four[0],four[3]);
 Check(coverage is {RegisteredTradingDays:4,AcceptedDays:3,MissingDays:1,CoveragePercent:75,State:"Incomplete"},"Coverage denominator uses calendar, missing day is not zero");
 Check(repository.Coverage(new("SZSE","159919"),four[0],four[3]) is {State:"NoCalendar",CoveragePercent:null},"No calendar cannot claim 100 percent coverage");
 Check(repository.Coverage(key,four[0].AddDays(-1),four[3]).State=="CalendarRangeIncomplete","Unregistered range boundaries remain incomplete");
 Reject(()=>repository.Coverage(key,four[3],four[0]),"Reversed coverage range rejected");
 File.WriteAllText(Path.Combine(qualityRoot,"raw",middleHash+".raw"),"tampered");
 coverage=repository.Coverage(key,four[0],four[3]);
 Check(coverage is {RegisteredTradingDays:4,CriticalDays:1,CalendarVerified:false,CoveragePercent:null},"Damaged calendar retains date boundary and critical state");
 Check(repository.Calendar("SSE").Count==0 && repository.Overview().Single(x=>x.Ticker==key.Ticker).Change1 is null,"Calendar damage pauses metrics instead of skipping a trading day");
 repository.ImportCalendar(Fetch("reviewed replacement calendar"),[("SSE",four[1])],CancellationToken.None);
 Check(repository.Coverage(key,four[0],four[3]) is {CalendarVerified:true,CriticalDays:0},"Reviewed calendar replacement restores current reference without deleting old evidence");
 repository.ImportShares(Fetch("suspicious day"),[new(key,four[3],250,"份")],CancellationToken.None);
 Check(repository.Coverage(key,four[0],four[3]).PendingDays==1,"Coverage separates pending review from missing");
 var rejected=repository.Evidence().Single(x=>x.State=="NeedsReview");repository.Review(rejected.RevisionId,false,"fixture reviewer");
 Check(repository.Coverage(key,four[0],four[3]).RejectedDays==1,"Coverage separates rejected data from missing");
 repository.ImportShares(Fetch("rechecked suspicious day"),[new(key,four[3],250,"份")],CancellationToken.None);
 var renewed=repository.Evidence().Single(x=>x.State=="NeedsReview");
 Check(renewed.Revision==2 && repository.Coverage(key,four[0],four[3]).PendingDays==1,"Same-value rejected observation can be submitted for a new review");
 repository.Review(renewed.RevisionId,true,"fixture reviewer",true);
 Check(repository.Coverage(key,four[0],four[3]) is {AcceptedDays:4,CoveragePercent:100,State:"CompleteRegisteredCalendar"},"Complete coverage is limited to the registered calendar scope");
}
// 导出必须核验同一份已读取字节，并保护原有目的地与插件私有存储。
var exportRoot=Path.Combine(root,"exports");Directory.CreateDirectory(exportRoot);
var exportRaw=new RawEvidenceStore(Path.Combine(exportRoot,"raw"));var content=Encoding.UTF8.GetBytes("官方原文件合成证据");var hash=exportRaw.Save(content);
var output=Path.Combine(exportRoot,"copy.bin");
await exportRaw.ExportVerifiedAsync(hash,output,[Path.Combine(exportRoot,"raw")],CancellationToken.None);
Check(File.ReadAllBytes(output).SequenceEqual(content),"Verified evidence export preserves exact original bytes");
using(var cancellation=new CancellationTokenSource())
{
 cancellation.Cancel();try {await exportRaw.ExportVerifiedAsync(hash,output,[],cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){ }
 Check(File.ReadAllBytes(output).SequenceEqual(content),"Cancelled evidence export preserves existing destination");
}
try {await exportRaw.ExportVerifiedAsync(hash,Path.Combine(exportRoot,"raw","forbidden.bin"),[Path.Combine(exportRoot,"raw")],CancellationToken.None);throw new Exception("Protected path accepted");}
catch(InvalidDataException){Console.WriteLine("PASS Evidence export rejects plugin private storage");}
File.WriteAllText(Path.Combine(exportRoot,"raw",hash+".raw"),"tampered");
try {await exportRaw.ExportVerifiedAsync(hash,output,[],CancellationToken.None);throw new Exception("Tampered evidence exported");}
catch(InvalidDataException){Console.WriteLine("PASS Damaged evidence export rejected");}
Check(File.ReadAllBytes(output).SequenceEqual(content) && !Directory.EnumerateFiles(exportRoot,"*.tmp").Any(),"Failed export leaves original destination and no temporary file");
// 模拟慢查询，验证用户切换证券或日期后，旧结果不能覆盖新输入的界面状态。
foreach(var changeSecurity in new[]{true,false})
{
 using var delayed=new DelayedCoverageRepository();
 await using var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>delayed,exportRaw);
 await workspace.InitializeAsync(CancellationToken.None);
 var model=new CRS.Plugins.NationalTeamWatch.Presentation.EtfViewModel(workspace,CancellationToken.None)
 {Selected=new("SSE","510300","合成样例","CSI300","DataPilot",null,null,null,null,null,"无数据",0)};
 var request=model.ShowCoverageCommand.ExecuteAsync(null);
 await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
 if(changeSecurity)model.Selected=new("SZSE","159919","第二合成样例","CSI300","DataPilot",null,null,null,null,null,"无数据",0);
 else model.CoverageFrom=model.CoverageFrom!.Value.AddDays(1);
 delayed.Release.Set();await request.WaitAsync(TimeSpan.FromSeconds(5));
 Check(model.CoverageDays.Count==0,changeSecurity?"Old coverage request cannot overwrite a newly selected ETF":"Old coverage request cannot overwrite a changed date range");
}

// 查询用例不需要路径、HTTP或后台作业能力；若意外访问这些能力，验证立即失败。
sealed class QueryOnlyContext:CRS.Plugin.Abstractions.IPluginHostContext
{
 public string PluginId=>"fixture";
 public CRS.Plugin.Abstractions.IPluginJobScheduler Jobs=>throw new NotSupportedException();
 public CRS.Plugin.Abstractions.IPluginDataPaths DataPaths=>throw new NotSupportedException();
 public CRS.Plugin.Abstractions.IPluginHttpGateway Http=>throw new NotSupportedException();
 public CRS.Plugin.Abstractions.IPluginDiagnostics Diagnostics=>throw new NotSupportedException();
}
// 仓储在返回结果前等待测试放行，使界面输入变化和后台查询返回的顺序可重复。
sealed class DelayedCoverageRepository:IMarketRepository
{
 public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public ManualResetEventSlim Release {get;}=new();
 public CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to)
 {
    Entered.SetResult();if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("合成查询没有放行。");
    return new(key.Market,key.Ticker,from,to,1,1,0,0,0,0,100,true,true,"CompleteRegisteredCalendar",[new(from,"Accepted","fixture")]);
 }
 public List<EtfSummary> Overview()=>throw new NotSupportedException();
 public List<EvidenceRow> Evidence()=>throw new NotSupportedException();
 public List<SharePoint> Points(EtfKey key)=>throw new NotSupportedException();
 public string RecordFetch(string hash,string sourceUrl,string? originalHash=null)=>throw new NotSupportedException();
 public void MarkFailed(string fetchId)=>throw new NotSupportedException();
 public void ImportCalendar(string fetchId,IReadOnlyList<(string Market,DateOnly Date)> days,CancellationToken cancellation)=>throw new NotSupportedException();
 public int ImportShares(string fetchId,IReadOnlyList<ImportRow> rows,CancellationToken cancellation)=>throw new NotSupportedException();
 public void Review(string revisionId,bool accept,string reviewer,bool comparableConfirmed=false)=>throw new NotSupportedException();
 public void SetMembership(EtfKey key,string name,string index,bool active,string reason)=>throw new NotSupportedException();
 public void Dispose()=>Release.Dispose();
}
