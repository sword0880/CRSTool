using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CRS.Application;
using CRS.Domain;
using CRS.Domain.Performance;
using CRS.Infrastructure;
using Dapper;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

internal static class PerformanceChecks
{
    private static readonly JsonSerializerOptions Json=new(){Converters={new JsonStringEnumConverter()}};
    private static readonly DateOnly From=new(2024,12,31),To=new(2025,12,31);
    private static PerformanceSource Source(string row)=>new("SYNTHETIC",row);
    private static ValuationSnapshot Nav(string id,DateOnly day,decimal value)=>new(id,"A",new(day.ToDateTime(new(23,59,59)),TimeSpan.Zero),day,ValuationMoment.EndOfDay,[new("Cash","USD",value)],Source(id),PerformanceQuality.Verified);
    private static PerformanceData Fixture()=>new(){Accounts=[new("A","IBKR","=HYPERLINK(合成)","USD",From)],Valuations=[Nav("OPEN",From,10000),Nav("END",To,11000)],Coverage=[new("A",From,To,true,Source("账单核对"),IncomeComplete:true,CostsComplete:true)],Prices=[new("UNUSED","DEMO","US","USD",To,1,Source("合成行情"))],Fx=[new("F0","USD","CNY",From,7,Source("期初汇率")),new("F1","USD","CNY",To,7,Source("期末汇率"))]};
    /// <summary>使用真实 SQLCipher 与恢复流程，但只用合成金额；真实样本仅输出结构和问题码。</summary>
    internal static async Task RunAsync(string root,bool realOnly=false)
    {
        const string password="Synthetic-Performance-Password-2026";
        var path=Path.Combine(root,"performance");using var vault=new VaultManager();var created=await vault.CreateAsync(path,password);await TestStores.Enroll(vault);
        var cases=vault.CreatePerformanceUseCases();
        if(realOnly){await RealSamples(cases);return;}
        using(var sharedVault=new VaultManager())
        {
            await sharedVault.CreateAsync(Path.Combine(root,"shared-performance"),password);await TestStores.Enroll(sharedVault);
            var sharedCases=sharedVault.CreatePerformanceUseCases();
            var sharedPath=Path.Combine(root,"shared-activity.xml");
            File.WriteAllText(sharedPath,"<FlexQueryResponse type=\"AF\"><FlexStatements><FlexStatement accountId=\"SHARED\" fromDate=\"20250101\" toDate=\"20251231\"><Trades/><CashTransactions/><OpenPositions/></FlexStatement></FlexStatements></FlexQueryResponse>");
            var originalHash=FrozenInput.Read(sharedPath).Sha256;
            var sharedOperations=new DesktopOperations(sharedVault.Repository,"",sharedVault);
            var parsed=sharedOperations.CreateCalculator(new SharedRates()).Calculate([sharedPath],2025,true,true);
            var shared=await sharedCases.LoadAsync();
            if(shared.Data.Accounts.Length!=1 || shared.Sources.Single().Hash!=originalHash || !parsed.Sources.Any(s=>s.Sha256.Equals(originalHash,StringComparison.OrdinalIgnoreCase))) throw new Exception("证券流水与绩效没有共用同一份原件。");
            sharedOperations.CreateCalculator(new SharedRates()).Calculate([sharedPath],2025,true,true);
            if((await sharedCases.LoadAsync()).Sources.Length!=1) throw new Exception("重算证券流水重复导入了绩效资料。");
        }
        var json=JsonSerializer.Serialize(Fixture(),Json);
        if(!await cases.ImportManualAsync(json) || await cases.ImportManualAsync(json))throw new Exception("PA-09 重复原件未去重。");
        var workspace=await cases.LoadAsync();
        if(workspace.Data.Valuations.Any(v=>v.Quality!=PerformanceQuality.Estimated))throw new Exception("手工输入自行升级成已核对。");
        await Reject(()=>cases.ImportManualAsync("{\"Accounts\":[null]}"));
        await Reject(()=>cases.ImportManualAsync("{\"Version\":1,\"Version\":1}"));
        await Reject(()=>cases.ImportManualAsync(JsonSerializer.Serialize(new PerformanceData{Fx=[new("BAD","USD","CNY",To,-1,Source("bad"))]},Json)));
        await Reject(()=>cases.ImportManualAsync(JsonSerializer.Serialize(new PerformanceData{Events=[new("BAD","MISSING",DateTimeOffset.UtcNow,DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date),InvestmentEventKind.Dividend,1,"USD",Source("bad"))]},Json)));
        var request=new PerformanceRequest(["A"],From,To,"USD");var run=await cases.CalculateAsync(request);
        if(run.Result.Metrics.Single(m=>m.Id=="Profit").Value!=1000 || run.Result.Exposure.Length!=2 || run.ResultSeal.Length!=64)throw new Exception("绩效结果或证据不完整。");
        await cases.SavePortfolioAsync(new("P","合成组合",["A"],"USD"));await cases.RenameAccountAsync("A","新的合成别名");await cases.ConfirmBaseCurrencyAsync("A","HKD");
        var original=await cases.OpenRunAsync(run.Id);if(original.Input.Accounts.Single().Alias!="=HYPERLINK(合成)" || original.Input.Accounts.Single().BaseCurrency!="USD")throw new Exception("当前元数据改写了历史。");
        var replay=await cases.ReplayAsync(run.Id);if(replay.ParentId!=run.Id || replay.InputHash!=run.InputHash || JsonSerializer.Serialize(replay.Result)!=JsonSerializer.Serialize(run.Result))throw new Exception("PA-15 冻结重放不同。");
        using(var canceled=new CancellationTokenSource()){canceled.Cancel();await Reject(()=>cases.CalculateAsync(request,canceled.Token));}
        if((await cases.LoadAsync()).History.Length!=2)throw new Exception("取消产生半份结果。");
        var ordinary=Path.Combine(root,"performance.xlsx");await Reject(()=>cases.ExportAsync(run.Id,ordinary,false,""));await Reject(()=>cases.ExportAsync(run.Id,ordinary,false,"",new(true)));
        await cases.ExportAsync(run.Id,ordinary,false,"",new(true,true));
        using(var book=new XSSFWorkbook(File.OpenRead(ordinary)))
        {
            var cell=book.GetSheet("账户汇总").GetRow(1).GetCell(0);
            if(cell.CellType!=CellType.String || cell.StringCellValue!="=HYPERLINK(合成)" || book.NumberOfSheets!=8)throw new Exception("PA-20 外部文本变成公式或底稿缺项。");
        }
        var encrypted=Path.Combine(root,"performance.crsexport");await cases.ExportAsync(run.Id,encrypted,true,password);
        if(File.ReadAllText(encrypted).Contains("合成") || File.ReadAllText(encrypted).Contains(run.Id))throw new Exception("密文外层泄漏绩效。");
        var restoredReport=EncryptedReportFiles.Restore(encrypted,root,password);
        if(!File.Exists(Path.Combine(restoredReport,"report.xlsx")) || !File.Exists(Path.Combine(restoredReport,"evidence.json")))throw new Exception("绩效加密底稿恢复失败。");
        var backup=Path.Combine(root,"performance.crsbak");await vault.BackupAsync(backup);
        using(var restored=new VaultManager())
        {
            var target=Path.Combine(root,"performance-restored");await restored.RestoreAsync(backup,target,created.RecoveryKey,true,password);await restored.UnlockAsync(target,password);await TestStores.Authenticate(restored);
            var recovered=restored.CreatePerformanceUseCases();var recoveredWorkspace=await recovered.LoadAsync();
            if(recoveredWorkspace.History.Length!=2 || recoveredWorkspace.Data.Prices.Length!=1 || recoveredWorkspace.Data.Fx.Length!=2 || recoveredWorkspace.Portfolios.Length!=1 || recoveredWorkspace.Sources.Length!=1 || (await recovered.OpenRunAsync(run.Id)).InputHash!=run.InputHash)throw new Exception("PA-17 便携备份遗漏绩效资料。");
            // 解密库中模拟原件损坏：摘要不一致必须阻断，不沿用派生结果掩盖错误。
            using(var c=restored.OpenForTest())c.Execute("UPDATE performance_documents SET raw=@raw",new{raw=Encoding.UTF8.GetBytes("damaged-source")});
            await Reject(()=>recovered.LoadAsync());
        }
        await cases.SetSourceIncludedAsync(workspace.Sources.Single().Hash,false,"合成排除测试");
        if((await cases.LoadAsync()).Data.Accounts.Length!=0 || (await cases.OpenRunAsync(run.Id)).Input.Accounts.Length!=1)throw new Exception("来源排除删除历史或原件。");
        await cases.SetSourceIncludedAsync(workspace.Sources.Single().Hash,true,"合成恢复测试");
        // 税务仓储写入与绩效表相互独立，不能污染冻结口径。
        vault.Repository.Save(new(){SnapshotId="SYNTHETIC-TAX",Year=2025,Summary=new(0,0,0,0,0,0,0)});
        if((await cases.OpenRunAsync(run.Id)).InputHash!=run.InputHash)throw new Exception("PA-19 税务写入改变绩效。");
        using(var c=vault.OpenForTest())
        {
            var payload=c.ExecuteScalar<byte[]>("SELECT payload FROM performance_runs WHERE id=@Id",run)!;var node=JsonNode.Parse(payload)!;node["Result"]!["EngineVersion"]="tampered";
            c.Execute("UPDATE performance_runs SET payload=@payload WHERE id=@Id",new{run.Id,payload=Encoding.UTF8.GetBytes(node.ToJsonString())});
        }
        await Reject(()=>cases.OpenRunAsync(run.Id));
        vault.Lock();await Reject(()=>cases.LoadAsync());await vault.BeginLocalUnlockAsync(path);await TestStores.Authenticate(vault);await Reject(()=>cases.LoadAsync());
        await Transfers(root,password);
        Console.WriteLine("绩效集成通过：SQLCipher、原件去重/格式拒绝、历史冻结与重放、取消、普通导出授权/TEMP 拒绝、公式注入、加密底稿、便携备份完整恢复、来源修订、税务隔离、原件/结果篡改及旧会话失效。");
    }
    private sealed class SharedRates:CRS.Application.Abstractions.IExchangeRateProvider
    {
        public string Fingerprint=>"shared-synthetic";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"合成","","",null,null,false,true);
        public List<AppliedRate> ForYear(int year)=>[];
    }
    private static async Task Transfers(string root,string password)
    {
        using var vault=new VaultManager();var created=await vault.CreateAsync(Path.Combine(root,"performance-transfers"),password);await TestStores.Enroll(vault);var cases=vault.CreatePerformanceUseCases();var day=new DateOnly(2025,6,30);
        // 示例独立于前一个库的不同合成汇率，不能让来源冲突被测试误判为引擎失败。
        await cases.ImportManualAsync(File.ReadAllText("docs/examples/performance.example.json"));
        var example=await cases.CalculateAsync(new(["demo-ibkr"],From,To));if(example.Result.Metrics.Single(m=>m.Id=="Profit").Value!=18000)throw new Exception("离线绩效文档模板不能重建历史价格/汇率。");
        ValuationSnapshot V(string id,string account,DateOnly date,decimal value)=>Nav(id,date,value) with{AccountId=account};
        var events=new[]{new InvestmentEvent("OUT","A",new(day.ToDateTime(new(12,0)),TimeSpan.Zero),day,InvestmentEventKind.Unknown,-50,"USD",Source("转出")),new InvestmentEvent("IN","B",new(day.ToDateTime(new(12,0)),TimeSpan.Zero),day,InvestmentEventKind.Unknown,50,"USD",Source("转入"))};
        var data=new PerformanceData{Accounts=[new("A","FUTU","合成 A","USD",From),new("B","IBKR","合成 B","USD",From)],Events=events,Valuations=[V("A0","A",From,100),V("A1","A",To,50),V("B0","B",From,0),V("B1","B",To,50)],Coverage=[new("A",From,To,true,Source("A完整")),new("B",From,To,true,Source("B完整"))]};
        await cases.ImportManualAsync(JsonSerializer.Serialize(data,Json));var original=await cases.CalculateAsync(new(["A","B"],From,To,"USD",ReturnMethod.EndOfDayReconstructed));
        // 重叠原件保留来源，但稳定业务事件不重复；不同经济金额则拒绝。
        await cases.ImportManualAsync(JsonSerializer.Serialize(new PerformanceData{Events=events},Json));if((await cases.LoadAsync()).Data.Events.Length!=2)throw new Exception("重叠原件重复计入稳定事件。");
        await Reject(()=>cases.ImportManualAsync(JsonSerializer.Serialize(new PerformanceData{Events=[events[0] with{Amount=-51}]},Json)));
        await Reject(()=>cases.ReviseClassificationAsync("OUT",InvestmentEventKind.CashTransfer,true,""));
        await Reject(()=>cases.ReviseClassificationAsync("OUT",InvestmentEventKind.Deposit,true,"不能把负数改成入金"));
        await Reject(()=>cases.ReviseClassificationAsync("IN",InvestmentEventKind.Buy,true,"缺少证券数量不能改成买入"));
        await cases.ReviseClassificationAsync("OUT",InvestmentEventKind.Borrowing,false,"合成分类草稿未确认");
        var draft=await cases.CalculateAsync(new(["A","B"],From,To,"USD"));if(draft.Result.Metrics.Single(m=>m.Id=="Profit").Value is not null)throw new Exception("未确认分类草稿绕过未知流量。");
        foreach(var e in events)await cases.ReviseClassificationAsync(e.Id,InvestmentEventKind.CashTransfer,true,"合成转账凭证已核对");
        await cases.ConfirmTransferAsync("OUT","IN",0,"合成两侧核对","USD");await Reject(()=>cases.ConfirmTransferAsync("OUT","IN",0,"重复关联","USD"));
        var merged=await cases.CalculateAsync(new(["A","B"],From,To,"USD",ReturnMethod.EndOfDayReconstructed));if(merged.Result.Flows.Count(f=>f.Internal)!=2 || merged.Result.Metrics.Single(m=>m.Id=="Profit").Value!=0)throw new Exception("分类修订后关联未生效。");
        if((await cases.OpenRunAsync(original.Id)).Input.Revisions.Length!=0)throw new Exception("分类修订改写旧输入。");
        var single=await cases.CalculateAsync(new(["A"],From,To,"USD"));if(single.Result.Flows.Single().Internal || single.Result.Metrics.Single(m=>m.Id=="NetInvestment").Value!=-50)throw new Exception("账户退出没有变为外部流量。");
        var backup=Path.Combine(root,"performance-transfer.crsbak");await vault.BackupAsync(backup);using var restored=new VaultManager();var path=Path.Combine(root,"performance-transfer-restored");await restored.RestoreAsync(backup,path,created.RecoveryKey,true,password);await restored.UnlockAsync(path,password);await TestStores.Authenticate(restored);
        var recovered=await restored.CreatePerformanceUseCases().LoadAsync();if(recovered.Data.Transfers.Length!=1 || recovered.Data.Revisions.Length!=3 || recovered.Data.Transfers.Single().ToleranceCurrency!="USD")throw new Exception("分类审计或容差币种未随备份恢复。");
    }
    private static async Task RealSamples(CRS.Application.Performance.IPerformanceUseCases cases)
    {
        var sample=Path.GetFullPath("Sample");if(!Directory.Exists(sample))throw new Exception("请在仓库根目录运行真实绩效验收。");
        var index=0;
        foreach(var (folder,broker,extension) in new[]{("IBKR","IBKR","*.xml"),("FUTU","FUTU","*.xlsx")})
        {
            var directory=Directory.GetDirectories(sample).FirstOrDefault(d=>Path.GetFileName(d).Equals(folder,StringComparison.OrdinalIgnoreCase));
            if(directory is null && broker=="FUTU")directory=Directory.GetDirectories(sample).FirstOrDefault(d=>Path.GetFileName(d).Contains("富途"));
            if(directory is null)throw new Exception("缺少券商样本目录："+broker);
            foreach(var file in Directory.GetFiles(directory,extension,SearchOption.AllDirectories))
            {
                if(broker=="FUTU")
                {
                    // 只看工作表结构区分年度活动与收入主表，不输出真实字段内容。
                    using var sourceBook=new XSSFWorkbook(File.OpenRead(file));
                    if(!Enumerable.Range(0,sourceBook.NumberOfSheets).Any(i=>sourceBook.GetSheetName(i).Contains("交易流水")))
                    {await Reject(()=>cases.ImportAsync(file,broker));Console.WriteLine("富途收入汇总：明确拒绝转为重复现金事件（PA-14）。");continue;}
                }
                var before=SHA256.HashData(File.ReadAllBytes(file));await cases.ImportAsync(file,broker);var w=await cases.LoadAsync();
                var from=w.Data.Accounts.Min(a=>a.TrustedFrom);var to=w.Data.Valuations.Select(v=>v.Date).DefaultIfEmpty(from.AddDays(1)).Max();if(to<=from)to=from.AddDays(1);
                var result=await cases.CalculateAsync(new(w.Data.Accounts.Select(a=>a.Id).ToArray(),from,to));
                if(!SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(before))throw new Exception("导入改写原件。");
                Console.WriteLine($"真实绩效样本 {++index} {broker}：累计账户 {w.Data.Accounts.Length}，事件 {w.Data.Events.Length}，净值快照 {w.Data.Valuations.Length}；问题码 {string.Join(',',result.Result.Issues.Select(i=>i.Code).Distinct().Order())}；未以程序值倒填独立对账。");
            }
        }
    }
    private static async Task Reject(Func<Task> action){try{await action();}catch(CrsException){return;}catch(OperationCanceledException){return;}throw new Exception("绩效应拒绝的操作被放行。");}
}
