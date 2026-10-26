using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
static void Check(bool condition,string name) {if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
static void Reject(Action action,string name) {try{action();}catch(Exception){Console.WriteLine("PASS "+name);return;}throw new Exception(name);}
if(args is ["--common-time"]){CommonTimeChecks.Run();return;}
if(args is ["--shared-read-admission"]){await PluginCommonAdmissionChecks.RunAsync();return;}
if(args is ["--bounded-content"]){await BoundedContentChecks.RunAsync();return;}
if(args is ["--public-storage"]){PublicResearchStorageChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.PublicResearchStorage",Guid.NewGuid().ToString("N")));return;}
if(args is ["--public-storage-wal-fixture",var walFixtureDirectory]){PublicResearchStorageChecks.WalFixture(walFixtureDirectory);return;}
if(args is ["--nav-research-checks"]){NavResearchChecks.Run();EtfNavStudyChecks.Run();return;}
if(args is ["--nav-storage-checks"]){NavStorageChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.NavStorage",Guid.NewGuid().ToString("N")));return;}
if(args is ["--nav-source-checks"]){await NavSourceChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.NavSource",Guid.NewGuid().ToString("N")));return;}
if(args is ["--nav-storage-sample",var navStorageInput,var navStorageOutput]){NavStorageTool.Run(navStorageInput,navStorageOutput);return;}
if(args is ["--nav-research-snapshot",var navInput,var navOutput]){NavResearchTool.Run(navInput,navOutput);return;}
if(args is ["--nav-research-verify",var navSnapshot]){NavResearchTool.Verify(navSnapshot);return;}
if(args is ["--nav-share-study-live",var studyNav,var studyFrom,var studyThrough,var studyOutput]){await EtfNavStudyTool.Capture(studyNav,studyFrom,studyThrough,studyOutput);return;}
if(args is ["--nav-share-study-verify",var frozenStudy]){EtfNavStudyTool.Verify(frozenStudy);return;}
if(args is ["--read-isolation"]){await ReadIsolationChecks.RunAsync(Path.Combine(Path.GetTempPath(),"CRS.ReadIsolation",Guid.NewGuid().ToString("N")));return;}
if(args is ["--repository-contracts"]){RepositoryContractChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.RepositoryContracts",Guid.NewGuid().ToString("N")));return;}
if(args is ["--evidence-adversarial"]){EvidenceAcceptanceAdversarialChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.EvidenceAdversarial",Guid.NewGuid().ToString("N")));return;}
if(args is ["--equity-status-live"]){await EquityStatusAuditTool.Run();return;}
if(args is ["--icbc-read-live",var icbcReadDirectory]){await IcbcEquityAcceptanceTool.Run(icbcReadDirectory,false);return;}
if(args is ["--icbc-review-live",var icbcReviewDirectory]){await IcbcEquityAcceptanceTool.Run(icbcReviewDirectory,true);return;}
if(args is ["--icbc-golden",var icbcGoldenDirectory]){IcbcGoldenChecks.Run(icbcGoldenDirectory);return;}
if(args is ["--abc-read-live",var abcReadDirectory]){await AbcEquityAcceptanceTool.Run(abcReadDirectory,false);return;}
if(args is ["--abc-golden",var abcGoldenDirectory]){AbcGoldenChecks.Run(abcGoldenDirectory);return;}
if(args is ["--gongniu-golden",var gongniuGoldenDirectory]){GongniuGoldenChecks.Run(gongniuGoldenDirectory);return;}
if(args is ["--gongniu-review-live",var gongniuReviewDirectory]){await GongniuEquityAcceptanceTool.Run(gongniuReviewDirectory,true);return;}
if(args is ["--gongniu-read-live",var gongniuReadDirectory]){await GongniuEquityAcceptanceTool.Run(gongniuReadDirectory,false);return;}
if(args is ["--cnd-comparison-golden",var cndComparisonDirectory]){CndComparisonGoldenChecks.Run(cndComparisonDirectory);return;}
if(args is ["--historical-pdf-checks",var historicalPdf]){HistoricalEquityChecks.Run(historicalPdf);return;}
if(args is ["--abc-review-live",var abcReviewDirectory]){await AbcEquityAcceptanceTool.Run(abcReviewDirectory,true);return;}
if(args is ["--deployment-state-live",var stateDirectory]){await DeploymentOperationsTool.State(stateDirectory);return;}
if(args is ["--research-feature-audit-live",var auditDirectory]){await ResearchFeatureAuditTool.RunAsync(auditDirectory);return;}
if(args is ["--equity-report-sync-live",var reportDirectory]){await DeploymentOperationsTool.Reports(reportDirectory,true);return;}
if(args is ["--equity-report-status-live",var reportStatusDirectory]){await DeploymentOperationsTool.Reports(reportStatusDirectory,false);return;}
if(args is ["--equity-report-sync-live",var reportTicker,var tickerReportDirectory]){await DeploymentOperationsTool.Reports(tickerReportDirectory,true,reportTicker);return;}
if(args is ["--equity-report-status-live",var statusTicker,var tickerStatusDirectory]){await DeploymentOperationsTool.Reports(tickerStatusDirectory,false,statusTicker);return;}
if(args is ["--etf-loading"]){await EtfLoadingChecks.Run();return;}
if(args is ["--dashboard-profile-checks"]){DashboardProfileChecks.Run();return;}
if(args is ["--dashboard-batch-checks"]){DashboardBatchChecks.Run();return;}
if(args is ["--acceptance-batch-checks"]){AcceptanceBatchChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.AcceptanceBatch",Guid.NewGuid().ToString("N")));return;}
if(args is ["--exit-alerts"]){var alertRoot=Path.Combine(Path.GetTempPath(),"CRS.ExitAlerts.Tests",Guid.NewGuid().ToString("N"));ExitAlertChecks.Run(alertRoot);ScopedEvidenceChecks.Run(Path.Combine(alertRoot,"scoped-evidence"));ExitAlertReplayChecks.Run();await ExitAlertJobChecks.Run(Path.Combine(alertRoot,"jobs"));return;}
if(args is ["--monitor-audit-live"]){await MonitorReadinessTool.Audit();return;}
if(args is ["--business-contracts"]){await BusinessContractChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.BusinessContracts",Guid.NewGuid().ToString("N")));return;}
if(args is ["--monitor-backfill-status"]){await MonitorReadinessTool.BackfillStatus();return;}
if(args is ["--monitor-summary-live"]){await MonitorReadinessTool.Summary();return;}
if(args is ["--monitor-apply-reviewed-metadata",var reviewFile]){await ReviewedFundMetadataTool.Apply(reviewFile);return;}
if(args is ["--monitor-backfill-live",var monitorFrom,var monitorThrough]){await MonitorReadinessTool.Backfill(monitorFrom,monitorThrough);return;}
if(args is ["--exit-alert-replay",var replayInput,var replayOutput]){await ExitAlertReplayTool.FromFile(replayInput,replayOutput);return;}
if(args is ["--exit-alert-replay-checks"]){ExitAlertReplayChecks.Run();return;}
if(args is ["--exit-alert-replay-installed",var replayFrom,var replayTo,var installedReplayOutput]){await ExitAlertReplayTool.Installed(replayFrom,replayTo,installedReplayOutput);return;}
if(args is ["--exit-alert-ui"]){ExitAlertUiChecks.Run();return;}
if(args is ["--exit-alert-installed"]){await ExitAlertInstalledProbe.Run();return;}
if(args is ["--market-cycle-checks"]){MarketCycleChecks.Run();MarketCycleApprovalRegistryChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.MarketCycleApproval",Guid.NewGuid().ToString("N")));return;}
if(args is ["--market-live"]){await MarketActivityChecks.RunLive(Path.Combine(Environment.CurrentDirectory,"artifacts","market-activity-live",Guid.NewGuid().ToString("N")));return;}
if(args is ["--market-sync-diagnostics"]){await MarketActivityChecks.RunSyncDiagnosticsAsync(Path.Combine(Path.GetTempPath(),"CRS.MarketSyncDiagnostics",Guid.NewGuid().ToString("N")));return;}
if(args is ["--scoped-evidence"]){ScopedEvidenceChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.ScopedEvidence",Guid.NewGuid().ToString("N")));return;}
if(args is ["--chart-gap-ui"]){EtfChartChecks.RunGapUi();return;}
if(args is ["--equity-ui"]){EquityUiChecks.Run();return;}
if(args is ["--p0-live-audit"]){await P0LiveAcceptanceChecks.Run();return;}
if(args is ["--p0-equity-case"]){await P0LiveAcceptanceChecks.EquityCase();return;}
if(args is ["--p0-golden",var goldenDirectory]){EquityGoldenChecks.Run(goldenDirectory);return;}
if(args is ["--p0-readiness-live"]){await P0LiveOperations.Readiness();return;}
if(args is ["--p0-collect-live"]){await P0LiveOperations.Collect();return;}
if(args is ["--p0-evidence-live"]){await P0LiveOperations.EvidenceCase();return;}
if(args is ["--p0-spot-check-live"]){await P0OfficialSpotChecks.Run();return;}
if(args is ["--p0-status-live"]){await P0LiveOperations.Status();return;}
if(args is ["--p0-maintenance-pause"]){await P0LiveOperations.MaintenancePlans(false);return;}
if(args is ["--p0-maintenance-resume"]){await P0LiveOperations.MaintenancePlans(true);return;}
if(args is ["--p0-review-live","--human-confirmed"]){await P0LiveOperations.ReviewConfirmedSample();return;}
if(args is ["--equity-live"]){await EquityLiveChecks.Run();return;}
if(args is ["--equity-report-sample",var reportSample]){EquityReportChecks.Real(reportSample);return;}
if(args.Length!=0)throw new ArgumentException("未知的验收参数，拒绝自动退回合成测试。");
NavResearchChecks.Run();
PublicResearchStorageChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.PublicResearchStorage",Guid.NewGuid().ToString("N")));
await IcbcSourceChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.IcbcSource",Guid.NewGuid().ToString("N")));
EtfNavStudyChecks.Run();
NavStorageChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.NavStorage",Guid.NewGuid().ToString("N")));
await NavSourceChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.NavSource",Guid.NewGuid().ToString("N")));
await EtfChartChecks.Run();
Check(EtfMetrics.Normalize(2489718.77m,"万份")==24897187700m,"DATA-01 decimal units");
Reject(()=>EtfMetrics.Normalize(1,"亿元"),"Unknown unit rejected");
var dates=Enumerable.Range(0,25).Select(x=>new DateOnly(2026,9,1).AddDays(x)).ToArray();
var points=dates.Select((date,i)=>new SharePoint(date,100+i,2+i/100m,i.ToString())).ToList();
Check(EtfMetrics.Calculate(dates[5],5,dates,points).EstimatedFlow==10.15m,"Daily amounts summed over window");
Check(EtfMetrics.Calculate(dates[5],1,dates,points.Where(x=>x.Date!=dates[4]).ToList()).State=="Incomplete","Adjacent day gap never skipped");
var missingNav=points.Select(x=>x.Date==dates[5]?x with{Nav=null}:x).ToArray();
Check(EtfMetrics.Calculate(dates[5],1,dates,missingNav) is {DeltaShares:1,EstimatedFlow:null},"Missing NAV preserves shares only");
Check(EtfMetrics.Calculate(dates[20],20,dates,points).Fingerprint==EtfMetrics.Calculate(dates[20],20,dates,points).Fingerprint,"Reproducible input fingerprint");
var basket=new[]{new EtfHistoryInput(new("SSE","510300"),dates,points),new EtfHistoryInput(new("SZSE","159919"),dates,points.Select(x=>x with{Shares=x.Shares*2}).ToArray())};
var totals=EtfHistory.Calculate(basket);
Check(totals[1] is {Shares:303,DeltaShares:3,Covered:2,Total:2},"All ETF same-date totals and daily net change");
var gap=EtfHistory.Calculate([basket[0],basket[1] with{Points=basket[1].Points.Where(x=>x.Date!=dates[1]).ToArray()}]);
Check(gap[1] is {Shares:null,DeltaShares:null,Covered:1} && gap[2].DeltaShares is null,"Missing member never becomes zero or a multi-day daily change");
Check(EtfHistory.Calculate([basket[0],basket[1] with{Calendar=[]}])[1] is {Shares:303,DeltaShares:null},"Missing calendar preserves total but withholds flow");
var differentCalendar=EtfHistory.Calculate([basket[0],basket[1] with{Calendar=dates.Where(x=>x!=dates[1]).ToArray()}]);
Check(differentCalendar[2].DeltaShares is null,"Different market predecessor dates cannot be summed");
Check(EtfHistory.Calculate([]).Count==0,"Empty monitored basket stays empty");
Reject(()=>ImportFormat.Shares("market,ticker,trade_date,total_shares,unit\nSSE,510300,2026-09-01,2,亿元"),"CSV unknown unit rejected");
Reject(()=>ImportFormat.Shares("market,ticker,trade_date,total_shares,unit\nSSE,510300,2026-09-01,2,份\nSSE,510300,2026-09-01,2,份"),"Duplicate file rows rejected");
var root=Path.Combine(Path.GetTempPath(),"CRS-ETF-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
await P0RemediationChecks.Run(root);
await ReadIsolationChecks.RunAsync(Path.Combine(root,"read-isolation"));
await BusinessContractChecks.Run(Path.Combine(root,"business-contracts"));
ExitAlertChecks.Run(Path.Combine(root,"exit-alerts"));
ScopedEvidenceChecks.Run(Path.Combine(root,"scoped-evidence"));
ExitAlertReplayChecks.Run();
await GapRecoveryChecks.Run(root);
MarketActivityChecks.Run(root);
MarketCycleChecks.Run();
await MarketActivityChecks.RunSyncDiagnosticsAsync(Path.Combine(root,"market-sync-diagnostics"));
DirectEquityChecks.Run(root);
HistoricalEquityChecks.CatalogContracts(Path.Combine(root,"historical-catalog"));
EvidenceAcceptanceAdversarialChecks.Run(Path.Combine(root,"evidence-adversarial"));
AcceptanceBatchChecks.Run(Path.Combine(root,"acceptance-batch"));
EquityReportChecks.Run(root);
await HistoryRangeChecks.Run(root);
EtfConfigurationChecks.Run(root);
// 合成官方响应验证字段、单位、日期及完整分页；不把联网能返回200当作解析验收。
var onlineDate=new DateOnly(2026,9,1);
var json="""{"actionErrors":[],"pageHelp":{"pageNo":1,"pageCount":1,"total":1},"result":[{"STAT_DATE":"2026-09-01","SEC_CODE":"510300","TOT_VOL":"123.45"}]}""";
var onlineParsed=SseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(json),onlineDate,1);
var szseJson="""[{"metadata":{"catalogid":"fund_jjgm","cols":{"current_size":"基金规模<br>(万份)"},"recordcount":1,"pagecount":1,"pageno":1},"data":[{"size_date":"2026-09-01","fund_code":"159919","current_size":"684,831.66"}],"error":null}]""";
Check(SzseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(szseJson),"159919",onlineDate,1).Rows.Single().Original==684831.66m,"SZSE official history parses dated ten-thousand shares and thousands separators");
Reject(()=>SzseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(szseJson.Replace("万份","亿元")),"159919",onlineDate,1),"SZSE amount column cannot masquerade as shares");
Reject(()=>SzseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(szseJson),"159915",onlineDate,1),"SZSE mismatched code rejected");
Reject(()=>SzseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(szseJson.Replace("684,831.66","68,4831.66")),"159919",onlineDate,1),"SZSE malformed grouping rejected");
Check(onlineParsed.Rows.Single().Original==123.45m && onlineParsed.Rows.Single().Unit=="万份","Official JSON shares use declared ten-thousand unit");
Reject(()=>SseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(json),onlineDate.AddDays(1),1),"Official response wrong date rejected");
Reject(()=>SseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(json.Replace("123.45","-1")),onlineDate,1),"Official response negative shares rejected");
Reject(()=>SseOfficialShareSource.Parse(Encoding.UTF8.GetBytes(json),onlineDate,2),"Official response wrong page rejected");
var onlineRoot=Path.Combine(root,"online");var onlineRaw=new RawEvidenceStore(Path.Combine(onlineRoot,"raw"));
await using(var onlineWorkspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(onlineRoot,"data"),onlineRaw),onlineRaw))
{
 await onlineWorkspace.InitializeAsync(CancellationToken.None);
 onlineWorkspace.SetOfficialSource(new SseOfficialShareSource(new FixtureHttp(json),onlineRaw));
 await onlineWorkspace.SyncAsync(onlineDate,onlineDate,CancellationToken.None);
 await onlineWorkspace.SyncAsync(onlineDate,onlineDate,CancellationToken.None);
 Check((await onlineWorkspace.PointsAsync(new("SSE","510300"),CancellationToken.None)).Single().Shares==1234500m,"Automatic download imports official shares without manual files and stays idempotent");
 Check((await onlineWorkspace.CoverageAsync(new("SSE","510300"),onlineDate,onlineDate,CancellationToken.None)).RegisteredTradingDays==0,"Automatic shares do not fabricate verified trading calendar");
 Check((await onlineWorkspace.EvidenceAsync(CancellationToken.None)).Single().OriginalHash!= (await onlineWorkspace.EvidenceAsync(CancellationToken.None)).Single().Hash,"Automatic source retains original JSON and normalized evidence separately");
}
try{await new SseOfficialShareSource(new FixtureHttp(json,403)).FetchAsync(onlineDate,CancellationToken.None);throw new Exception("403 accepted");}catch(InvalidDataException){Console.WriteLine("PASS Official HTTP403 stops collection without fake facts");}
try{await new SseOfficialShareSource(new FixtureHttp(json.Replace("\"total\":1","\"total\":2"))).FetchAsync(onlineDate,CancellationToken.None);throw new Exception("Truncated page accepted");}catch(InvalidDataException){Console.WriteLine("PASS Truncated official pagination rejected");}
// 宿主取消必须传递到界面调度出的采集任务，不能只取消Quartz外层等待。
var cancelRaw=new RawEvidenceStore(Path.Combine(root,"cancel-raw"));var blockingSource=new BlockingOfficialSource();
await using(var cancelWorkspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"cancel-data"),cancelRaw),cancelRaw))
{
 await cancelWorkspace.InitializeAsync(CancellationToken.None);cancelWorkspace.SetOfficialSource(blockingSource);
 var cancelModel=new CRS.Plugins.NationalTeamWatch.Presentation.EtfViewModel(cancelWorkspace,CancellationToken.None);using var hostCancel=new CancellationTokenSource();
 var work=cancelModel.SyncLatestAsync(hostCancel.Token);await blockingSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));hostCancel.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(5));
 Check(!cancelModel.Busy && cancelModel.Status.Contains("取消"),"Host scheduler cancellation reaches active plugin network operation");
}
var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
using(var repository=TestMarketData.Open(Path.Combine(root,"data"),raw))
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
 repository.SetMembership(new("SSE","600020"),"extra","index",true,"test");
 Check(repository.Overview().Count==40,"All 24 default ETFs plus 16 test ETFs remain active; row capacity is presentation only");
 repository.Snapshot(Path.Combine(root,"snapshot.db"));Check(File.Exists(Path.Combine(root,"snapshot.db")),"WAL-safe SQLite backup snapshot");
 File.WriteAllText(Path.Combine(root,"raw",candidate.Hash+".raw"),"tampered");
 repository.ReconcileEvidenceIntegrity();
 Check(repository.Points(new("SSE","510300")).Count==1,"DATA-09 tampered raw data excluded");
 Reject(()=>repository.ImportShares(Fetch("unknown date"),[new(new("SSE","510300"),new(2026,8,1),1,"份")],CancellationToken.None),"Unreviewed calendar dates rejected");
}
Console.WriteLine("ETF deterministic/offline integration checks passed.");
// 使用分日历证据的合成数据复现“中间日损坏后被跳过”的问题；这些日期不是实际交易所日历。
var qualityRoot=Path.Combine(root,"coverage");var qualityRaw=new RawEvidenceStore(Path.Combine(qualityRoot,"raw"));
using(var repository=TestMarketData.Open(Path.Combine(qualityRoot,"data"),qualityRaw))
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
 repository.ReconcileEvidenceIntegrity();
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
 using var delayed=new DelayedCoverageRepository(Path.Combine(exportRoot,Guid.NewGuid().ToString("N")),exportRaw);
 await using var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>delayed.Repository,exportRaw);
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
// 测试只返回固定字节，不向真实交易所发请求或使用用户网络凭据。
sealed class FixtureHttp(string content,int status=200):CRS.Plugin.Abstractions.IPluginHttpGateway
{
 public Task<CRS.Plugin.Abstractions.PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>Task.FromResult(new CRS.Plugin.Abstractions.PublicHttpResponse(status,Encoding.UTF8.GetBytes(content),"application/json"));
}
sealed class BlockingOfficialSource:IOfficialShareSource
{
 public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public async Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)
 {Entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return new(day,[]);}
}
// 用真实存储转发完整契约，只在覆盖查询前插入可控等待；测试不再依赖不完整仓储默认实现。
sealed class DelayedCoverageRepository : IDisposable
{
 public IMarketRepository Repository {get;}
 public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public ManualResetEventSlim Release {get;}=new();
 public DelayedCoverageRepository(string directory,RawEvidenceStore raw)
 {
    var key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);IMarketRepository inner;
    try{inner=new MarketRepository(directory,raw,approvalAuditKey:key);}
    finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);}
    Repository=System.Reflection.DispatchProxy.Create<IMarketRepository,DelayedRepositoryProxy>();
    var proxy=(DelayedRepositoryProxy)Repository;proxy.Inner=inner;
    proxy.BeforeCoverage=()=>{Entered.SetResult();if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("合成查询没有放行。");};
 }
 public void Dispose(){Repository.Dispose();Release.Dispose();}
}
// 代理仅供这项界面竞态测试使用；未拦截的方法全部转发真实实现，异常保留原类型。
class DelayedRepositoryProxy : System.Reflection.DispatchProxy
{
 public IMarketRepository Inner {get;set;}=null!;
 public Action BeforeCoverage {get;set;}=null!;
 protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
 {
    if(method!.Name==nameof(IEtfReadRepository.Coverage))
    {
        BeforeCoverage();var key=(EtfKey)args![0]!;var from=(DateOnly)args[1]!;var to=(DateOnly)args[2]!;
        return new CoverageReport(key.Market,key.Ticker,from,to,1,1,0,0,0,0,100,true,true,"CompleteRegisteredCalendar",[new(from,"Accepted","fixture")]);
    }
    try{return method.Invoke(Inner,args);}
    catch(System.Reflection.TargetInvocationException error) when(error.InnerException is {} cause)
    {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();throw;}
 }
}
