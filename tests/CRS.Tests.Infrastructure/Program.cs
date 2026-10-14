using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
if(args is ["--capture-performance-imports"]) {PerformanceImportRegression.Run(true);return;}
var root=Path.Combine(Path.GetTempPath(),"CRS-Layers-"+Guid.NewGuid().ToString("N"));
// 绩效资料和税务相互独立，专项验收使用新的合成加密库。
if(args is ["--performance-only"]) {await PerformanceChecks.RunAsync(root);return;}
if(args is ["--performance-real-only"]) {await PerformanceChecks.RunAsync(root,true);return;}
// Hello 的操作系统交互使用替身，库、备份及本机槽均执行真实实现。
if(args is ["--hello-only"]) {await WindowsHelloChecks.RunAsync(root); return;}
if(args is ["--phone-settings-only"]) {await PhoneSettingsChecks.RunAsync(root); return;}
// 目录清理等局部修正可单独复验加密容器，不重复运行无关的认证与数据库场景。
if(args is ["--encrypted-export-only"]) {await EncryptedExportChecks.RunAsync(root); return;}
// 局部结构升级与归档故障演练可独立运行，仍只使用隔离的加密库。
if(args is ["--schema-upgrade-only"]) {await SchemaUpgradeChecks.RunAsync(root); return;}
// 大库和恶意容器边界可独立验证，避免反复执行全部认证用例。
if(args is ["--resource-limits-only"]) {await ResourceProtectionChecks.RunAsync(root); return;}
// 可在真实锁屏演练前独立验证原生备份的中断与旧任务隔离。
if(args is ["--lock-interruption-only"]) {await LockInterruptionChecks.RunAsync(root); return;}
// 期间清单、加密持久化及底稿可单独复验，不重复无关认证场景。
if(args is ["--account-periods-only"]) {AccountPeriodFileChecks.Run(root); return;}
// 敏感导出策略可单独检查，不重复无关认证测试。
if(args is ["--sensitive-exports-only"]) {await SensitiveExportChecks.RunAsync(root); return;}
var store=TestStores.Create(root);
var oldJson="""{"Year":2025,"Broker":"FUTU","Summary":{"DividendCny":100,"InterestCny":25,"GainCny":200,"DividendInterestTax":25,"GainTax":40,"ForeignCredit":5,"SupplementTax":60},"SnapshotId":"SYNTHETIC-OLD"}""";
var old=JsonSerializer.Deserialize<CalculationResult>(oldJson)!; store.Save(old);
if (store.Load(old.SnapshotId).Summary.SupplementTax!=60m || store.History().Single().Broker!="FUTU") throw new Exception("旧结果 JSON / 历史索引不兼容。");
var trade=new Trade(new("SYNTHETIC","DEMO","USD"),"DEMO",DateTimeOffset.UtcNow,true,"BUY",1,1,1,0,-1,"B","synthetic",1,"STK",null);
var session=new CalculationResult { Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="SYNTHETIC-SESSION",SessionTrades=[trade],ImportedTradeCount=1 };
store.Save(session);
var loaded=store.Load(session.SnapshotId);
if (loaded.SessionTrades.Count!=0 || loaded.ImportedTradeCount!=1 || JsonSerializer.Serialize(session).Contains("SessionTrades")) throw new Exception("未选择完整快照却持久化交易明细。");
var config=Path.Combine(root,"config");Directory.CreateDirectory(config);
File.WriteAllText(Path.Combine(config,"exchange_rate.json"),"""{"2025":{"USD":7,"CNY":1}}""");
File.WriteAllText(Path.Combine(config,"exchange_rate_sources.json"),"{}");
var rates=new ExchangeRates(config);
if (rates.ForYear(2025).Count!=2 || rates.ForYear(2024).Count!=0 || rates.Get(2025,"USD").SourceConfirmed) throw new Exception("汇率列表猜测了来源或缺失年度。");
Console.WriteLine("Infrastructure 验证通过：旧 JSON、交易隐私、年度汇率列表（3 项）。");
ImportConsistencyChecks.Run(root);
AccountPeriodFileChecks.Run(root);
await SettingsChecks.RunAsync(root);
HistoryPagingChecks.Run(root);
await AnnualAggregationChecks.RunAsync(root);
await ForeignCreditChecks.RunAsync(root);
await VaultChecks.RunAsync(root);
await MfaChecks.RunAsync(root);
await LocalKeyChecks.RunAsync(root);
await PhoneSettingsChecks.RunAsync(root);
await WindowsHelloChecks.RunAsync(root);
await PerformanceChecks.RunAsync(root);
await ExportChecks.RunAsync(root);
await SensitiveExportChecks.RunAsync(root);
await EncryptedExportChecks.RunAsync(root);
await SchemaUpgradeChecks.RunAsync(root);
await ResourceProtectionChecks.RunAsync(root);
await LockInterruptionChecks.RunAsync(root);
