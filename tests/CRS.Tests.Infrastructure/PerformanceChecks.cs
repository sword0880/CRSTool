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
        PerformanceImportRegression.Run();
        BrokerSourceFactsChecks.Run(Path.Combine(root,"broker-source-facts"));
        await BrokerProvenanceChecks.RunAsync(Path.Combine(root,"broker-provenance"));
        await PerformanceManualInputChecks.RunAsync(root);
        const string password="Synthetic-Performance-Password-2026";
        await CheckMarketAsync();
        await CheckFutuLabelsAsync(root);
        await CheckFutuOverviewAsync(root);
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
            using var sharedImport=sharedOperations.BeginCalculation(new SharedRates(),"IBKR",2025);
            var parsed=sharedImport.Calculator.Calculate([sharedPath],2025,true,true);sharedImport.Commit(parsed,default);
            var shared=await sharedCases.LoadAsync();
            if(shared.AccountIdentities.Single().AccountNumber!="SHARED")throw new Exception("IBKR 展示账号未从真实原件取得。");
            if(shared.Data.Accounts.Length!=1 || shared.Sources.Single().Hash!=originalHash || !parsed.Sources.Any(s=>s.Sha256.Equals(originalHash,StringComparison.OrdinalIgnoreCase))) throw new Exception("证券流水与绩效没有共用同一份原件。");
            using(var repeat=sharedOperations.BeginCalculation(new SharedRates(),"IBKR",2025)){var repeated=repeat.Calculator.Calculate([sharedPath],2025,true,true);repeat.Commit(repeated,default);}
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
        if(CRS.Application.Performance.PerformanceSnapshot.Identity(run.Input)!=PerformanceCodec.Hash(PerformanceCodec.Encode(PerformanceCodec.Ordered(run.Input))))throw new Exception("新快照身份与既有历史摘要格式不兼容。");
        var manifest=CRS.Application.Performance.PerformanceRunManifests.From(run);
        if(manifest.InputSnapshotId!=run.InputHash || manifest.AlgorithmVersion!=run.Result.EngineVersion || manifest.CostMethodVersion!=run.Result.CostMethodVersion || manifest.ExternalDatasets.Length!=2 || !manifest.NormalizationVersion.StartsWith("Unrecorded",StringComparison.Ordinal))throw new Exception("运行清单遗漏来源/版本或伪造历史解析版本。");
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
        await vault.SetDocumentEncryptionPasswordAsync(password);
        var backup=Path.Combine(root,"performance.crsbak");await vault.BackupAsync(backup);
        using(var restored=new VaultManager())
        {
            var target=Path.Combine(root,"performance-restored");await restored.RestoreAsync(backup,target,created.RecoveryKey,true,password);await restored.UnlockAsync(target,password);await TestStores.Authenticate(restored);
            var recovered=restored.CreatePerformanceUseCases();var recoveredWorkspace=await recovered.LoadAsync();
            if(restored.DocumentEncryptionPassword!=password)throw new Exception("统一文档密码未随加密备份恢复。");
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
    private static async Task CheckMarketAsync()
    {
        await PerformanceMarketConcurrencyChecks.RunAsync();
        using var http=new HttpClient(new MarketHandler());var provider=new YahooPerformanceMarketData(http);
        var document=await provider.FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"CNY",default);
        if(document.Data.Prices.Count(p=>p.Market=="INDEX")!=2 || document.Data.Prices.Count(p=>p.Market=="INDEX_FX")!=2 || document.Data.Fx.Length!=0 || document.Hash!=PerformanceCodec.Hash(document.Raw))throw new Exception("联网指数和汇率未冻结来源，或混入组合汇率。");
        var shanghai=await provider.FetchAsync("SSE",new(2025,1,2),new(2025,1,3),"CNY",default);
        if(shanghai.Data.Prices.Length!=2 || shanghai.Data.Prices.Any(p=>p.Security!="SSE" || p.Currency!="CNY" || p.Market!="INDEX") || !Encoding.UTF8.GetString(shanghai.Raw).Contains("000001.SS"))throw new Exception("上证指数代码、人民币报价或免换汇处理不正确。");
        var shanghaiUsd=await provider.FetchAsync("SSE",new(2025,1,2),new(2025,1,3),"USD",default);
        if(shanghaiUsd.Data.Prices.Count(p=>p.Market=="INDEX_FX" && p.Security=="CNYUSD" && p.Currency=="USD")!=2)throw new Exception("上证指数美元对比未请求人民币兑美元汇率。");
        using var summerHttp=new HttpClient(new MarketHandler(true));var summer=await new YahooPerformanceMarketData(summerHttp).FetchAsync("SPX",new(2025,6,2),new(2025,6,3),"USD",default);
        if(summer.Data.Prices.Min(p=>p.Date)!=new DateOnly(2025,6,2))throw new Exception("行情未按市场时区确定交易日期。");
    }
    private static async Task CheckFutuLabelsAsync(string root)
    {
        using var book=new XSSFWorkbook();var sheet=book.CreateSheet("账户信息");
        var header=sheet.CreateRow(0);header.CreateCell(0).SetCellValue("年份");header.CreateCell(1).SetCellValue("账户号码");header.CreateCell(2).SetCellValue("账户名称");
        var row=sheet.CreateRow(1);row.CreateCell(0).SetCellValue("2025");row.CreateCell(1).SetCellValue("12345678");row.CreateCell(2).SetCellValue("合成港股账户");
        using var output=new MemoryStream();book.Write(output,true);var raw=output.ToArray();
        var id="FUTU:"+PerformanceCodec.Hash(Encoding.UTF8.GetBytes("FUTU:12345678"))[..24];
        var labels=PerformanceBrokerImporter.FutuAccountLabels(FrozenInput.FromBytes("legacy.xlsx",raw));
        if(!labels[id].Contains("12345678") || !labels[id].Contains("合成港股账户"))throw new Exception("富途显示名称未包含报表真实账户。");
        using var vault=new VaultManager();await vault.CreateAsync(Path.Combine(root,"futu-alias"),"Synthetic-Legacy-Account-Password");await TestStores.Enroll(vault);
        var cases=vault.CreatePerformanceUseCases(false);
        var data=new PerformanceData{Accounts=[new(id,"FUTU","富途账户 "+id[^6..],"HKD",new(2025,1,1),BaseCurrencyConfirmed:false)]};
        using(var c=vault.OpenForTest()) c.Execute("INSERT INTO performance_documents(hash,name,format,raw,data) VALUES(@hash,'legacy.xlsx','.xlsx',@raw,@data)",new{hash=PerformanceCodec.Hash(raw),raw,data=PerformanceCodec.Encode(data)});
        var legacy=await cases.LoadAsync();
        if(!legacy.Data.Accounts.Single().Alias.Contains("12345678") || legacy.AccountIdentities.Single().AccountNumber!="12345678")throw new Exception("旧版富途摘要别名或真实账号读取不正确。");
        var privateDisplay=new CRS.Application.Performance.PerformanceAccountPrivacy(legacy.Data.Accounts,legacy.AccountIdentities);
        if(privateDisplay.Account(legacy.Data.Accounts.Single()).Contains("12345678") || !privateDisplay.Account(legacy.Data.Accounts.Single()).Contains("5678"))throw new Exception("富途默认展示未遮蔽账号。");
        await cases.RenameAccountAsync(id,"我的港股账户");
        if((await cases.LoadAsync()).Data.Accounts.Single().Alias!="我的港股账户")throw new Exception("用户账户别名被自动修正覆盖。");
    }

    private static async Task CheckFutuOverviewAsync(string root)
    {
        static void Sheet(XSSFWorkbook book, string name, params string[][] rows)
        {
            var sheet = book.CreateSheet(name);
            for (var row = 0; row < rows.Length; row++)
            {
                var cells = sheet.CreateRow(row);
                for (var column = 0; column < rows[row].Length; column++) cells.CreateCell(column).SetCellValue(rows[row][column]);
            }
        }
        string WriteReport(string name, string year, string[][] accounts, string[][] positions, string[][] cash)
        {
            using var book = new XSSFWorkbook();
            Sheet(book, "账户信息", [["年份", "账户号码", "账户名称"], .. accounts.Select(a => new[] { year, a[0], a[1] })]);
            Sheet(book, "交易流水", ["成交时间", "品类", "代码名称", "方向", "数量/面值", "价格", "币种", "账户号码", "交易所/市场"]);
            Sheet(book, "持仓总览", [["时期类型", "日期", "账户号码", "品类", "代码名称", "交易所/市场", "币种", "数量/面值", "市值"], .. positions]);
            Sheet(book, "资金总览", [["时期类型", "日期", "账户号码", "币种", "金额"], .. cash]);
            var path = Path.Combine(root, name);
            using var stream = File.Create(path);
            book.Write(stream, true);
            return path;
        }

        var file = WriteReport("futu-overview.xlsx", "2025",
            [["DEMO1", "多币种账户"], ["DEMO2", "空余额账户"], ["DEMO3", "空白基金账户"], ["DEMO4", "异常基金账户"]],
            [
            ["期末", "20251231", "DEMO1", "证券", "DEMO-HK", "HK", "HKD", "2", "80"],
            ["期末", "20251231", "DEMO1", "证券", "DEMO-US", "US", "USD", "3", "150"],
            ["期末", "20251231", "DEMO1", "基金", "DEMO-FUND", "", "USD", "2", "40"],
            ["期末", "20251231", "DEMO2", "证券", "DEMO-HK", "HK", "HKD", "1", "10"],
            ["期末", "20251231", "DEMO3", "基金", "", "", "", "", ""],
            ["期末", "20251231", "DEMO4", "基金", "DEMO-INVALID", "", "HKD", "1", "异常"]
            ],
            [
            ["期末", "20251231", "DEMO1", "HKD", "20"], ["期末", "20251231", "DEMO1", "USD", "-5"],
            ["期末", "20251231", "DEMO2", "HKD", "0"], ["期末", "20251231", "DEMO2", "USD", ""],
            ["期末", "20251231", "DEMO3", "HKD", "5"], ["期末", "20251231", "DEMO4", "HKD", "6"]
            ]);
        var next = WriteReport("futu-overview-next.xlsx", "2026", [["DEMO1", "多币种账户"]],
            [["期初", "20251231", "DEMO1", "证券", "DEMO-HK", "HK", "HKD", "2", "999"]],
            [["期初", "20251231", "DEMO1", "HKD", "500"]]);

        using var vault = new VaultManager();
        await vault.CreateAsync(Path.Combine(root, "futu-overview"), "Synthetic-Overview-Password");
        await TestStores.Enroll(vault);
        var cases = vault.CreatePerformanceUseCases(false);
        await cases.ImportAsync(next, "FUTU");
        await cases.ImportAsync(file, "FUTU");
        var workspace = await cases.LoadAsync();
        if (workspace.Data.Valuations.SelectMany(v => v.Components).Any(c => c.Kind == "Security" && c.Amount.HasValue)
            || workspace.AccountOverviewValuations is not { } valuations)
            throw new Exception("归档市值未进入账户汇总，或改变了原绩效事实。");
        var rows = PerformanceAccountValuations.Latest(workspace.Data with { Valuations = valuations }, To);
        var first = rows[workspace.AccountIdentities.Single(i => i.AccountNumber == "DEMO1").AccountId];
        var second = rows[workspace.AccountIdentities.Single(i => i.AccountNumber == "DEMO2").AccountId];
        var blankFund = rows[workspace.AccountIdentities.Single(i => i.AccountNumber == "DEMO3").AccountId];
        var invalidFund = rows[workspace.AccountIdentities.Single(i => i.AccountNumber == "DEMO4").AccountId];
        if (!first.NetAssetsByCurrency.SequenceEqual([new("HKD", 100), new("USD", 185)])
            || !first.CashByCurrency.SequenceEqual([new("HKD", 20), new("USD", -5)])
            || second.Cash is not null || !second.CashByCurrency.SequenceEqual([new("HKD", 0), new("USD", null)])
            || second.NetAssets is not null || second.NetAssetsByCurrency.Length != 0
            || second.NetAssetsUnavailableReason != "缺现金余额"
            || blankFund.NetAssets != 5 || invalidFund.NetAssetsUnavailableReason != "资产资料不完整")
            throw new Exception("归档市值、负现金或空白余额未正确投影到账户汇总。");
        var annual = PerformanceAccountValuations.Annual(workspace.Data with { Valuations = valuations }, To,
            [new("HKD", "HKD", "CNY", To, 1, Source("HKD")), new("USD", "USD", "CNY", To, 7, Source("USD"))]).Last();
        if (annual.TotalCny is not null || annual.Brokers.Single() is not { NetAssetsCny: 1400, CoveredAccounts: 2, ExpectedAccounts: 4 })
            throw new Exception("基金估值或空白基金金额未正确计入年度资产，或缺项被当成完整覆盖。");
        var conflict = WriteReport("futu-overview-conflict.xlsx", "2025", [["DEMO1", "多币种账户"]],
            [["期末", "20251231", "DEMO1", "证券", "DEMO-HK", "HK", "HKD", "2", "700"]],
            [["期末", "20251231", "DEMO1", "HKD", "200"]]);
        await cases.ImportAsync(conflict, "FUTU");
        var conflicted = await cases.LoadAsync();
        var conflictRow = PerformanceAccountValuations.Latest(conflicted.Data with { Valuations = conflicted.AccountOverviewValuations! }, To)
            [conflicted.AccountIdentities.Single(i => i.AccountNumber == "DEMO1").AccountId];
        if (conflictRow.NetAssetsUnavailableReason != "同日估值资料冲突")
            throw new Exception("两份有差异的期末原件被静默取舍。");
        Console.WriteLine("账户汇总归档验证通过：期末优先、基金市值与空白、负现金、缺余额及期末冲突。");

        var weekend = WriteReport("futu-weekend.xlsx", "2023",
            [["WK1", "完整账户"], ["WK2", "缺同日现金账户"], ["WK3", "缺估值账户"]],
            [["期末", "20231229", "WK1", "证券", "DEMO-HK", "HK", "HKD", "2", "80"],
             ["期末", "20231229", "WK1", "基金", "DEMO-FUND", "", "HKD", "2", "40"],
             ["期末", "20231229", "WK2", "证券", "DEMO-HK", "HK", "HKD", "1", "10"]],
            [["期末", "20231229", "WK1", "HKD", "-5"], ["期末", "20231228", "WK2", "HKD", "20"]]);
        var weekendOpening = WriteReport("futu-weekend-opening.xlsx", "2024", [["WK1", "完整账户"]],
            [["期初", "20231229", "WK1", "证券", "DEMO-HK", "HK", "HKD", "2", "999"]],
            [["期初", "20231229", "WK1", "HKD", "500"]]);
        await cases.ImportAsync(weekendOpening, "FUTU");
        await cases.ImportAsync(weekend, "FUTU");
        var shifted = await cases.LoadAsync();
        var friday = new DateOnly(2023, 12, 29);
        var calendarEnd = new DateOnly(2023, 12, 31);
        var overview = shifted.Data with { Valuations = shifted.AccountOverviewValuations! };
        var weekendAnnual = PerformanceAccountValuations.Annual(overview, To,
            [new("FRI", "HKD", "CNY", friday, 2, Source("同日汇率")),
             new("SUN", "HKD", "CNY", calendarEnd, 3, Source("不同日汇率"))]).Single(y => y.Year == 2023);
        if (shifted.Data.Valuations.Any(v => v.Date == friday) || weekendAnnual.Date != friday ||
            weekendAnnual.TotalCny is not null || weekendAnnual.Brokers.Single() is not { NetAssetsCny: 230, CoveredAccounts: 1, ExpectedAccounts: 3 })
            throw new Exception("周末年末没有用券商实际日期合计同日持仓和现金，或错误混入其他日期/期初资料。");
        Console.WriteLine("富途周末年末汇总通过：使用周五日期、证券和基金市值、同日现金和汇率，保留覆盖缺口。");
    }

    private sealed class MarketHandler(bool summer=false):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {
            if(request.RequestUri!.Host!="query1.finance.yahoo.com")throw new Exception("行情请求发送到错误地址。");
            var first=summer?new DateTimeOffset(2025,6,1,23,0,0,TimeSpan.Zero).ToUnixTimeSeconds():1735776000L;
            var json=JsonSerializer.Serialize(new{chart=new{result=new[]{new{meta=new{exchangeTimezoneName=summer?"Europe/London":"UTC"},timestamp=new[]{first,first+86400},indicators=new{quote=new[]{new{close=new[]{100,110}}}}}}}});
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(json)});
        }
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
        var workspace = await cases.LoadAsync();
        var friday = new DateOnly(2023, 12, 29);
        var overview = workspace.Data with { Valuations = workspace.AccountOverviewValuations! };
        // 仅验证归档报表能否提供完整的同日组成，合成汇率不用于输出金额。
        var annual = PerformanceAccountValuations.Annual(overview, To,
            [new("SAMPLE-HKD", "HKD", "CNY", friday, 1, Source("测试汇率")),
             new("SAMPLE-USD", "USD", "CNY", friday, 1, Source("测试汇率"))]).Single(y => y.Year == 2023);
        var futu = annual.Brokers.Single(b => b.Broker == "FUTU");
        if (annual.Date != friday || futu.CoveredAccounts == 0 || futu.NetAssetsCny is null)
            throw new Exception("富途 2023 原表的 12 月 29 日估值仍未进入年度资产明细。");
        Console.WriteLine($"富途 2023 原表估值日 {annual.Date:yyyy-MM-dd}，可用同日组成覆盖 {futu.CoveredAccounts}/{futu.ExpectedAccounts} 个账户。");
    }
    private static async Task Reject(Func<Task> action){try{await action();}catch(CrsException){return;}catch(OperationCanceledException){return;}throw new Exception("绩效应拒绝的操作被放行。");}
}
