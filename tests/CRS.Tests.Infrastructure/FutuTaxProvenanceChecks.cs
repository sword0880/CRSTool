using System.Runtime.Loader;
using System.Text.Json;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using NPOI.XSSF.UserModel;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class FutuTaxProvenanceChecks
{
    public static async Task RunAsync(string root,string? baseline=null)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS 富途税务版本："+label);}
        void Reject(Action work,string label)
        {try{work();throw new Exception("意外允许");}catch(CrsException){Check(true,label);}}
        var income=Path.Combine(root,"income.xlsx");WriteIncome(income);
        var pdf=Path.Combine(root,"income.pdf");WritePdf(pdf);
        var trades=Path.Combine(root,"trades.xlsx");File.Copy("tests/fixtures/futu_performance.xlsx",trades);
        var excelData=new FutuImporter(2025).Parse([income]);
        var pdfData=new FutuImporter(2025).Parse([pdf]);
        Check(excelData.ImportProvenance is{Count:1} x&&x[0].ReaderVersion=="FUTU.IncomeExcel.v1","Excel收入记录实际读取版本");
        Check(pdfData.ImportProvenance is{Count:1} p&&p[0].ReaderVersion=="FUTU.IncomePdf.v1","PDF收入使用独立读取版本");
        Check(excelData.AnnualIncome.Single().Dividend==100&&pdfData.AnnualIncome.Single().Dividend==100
            &&excelData.AnnualIncome.Single().Interest==5&&pdfData.AnnualIncome.Single().Interest==5,"两种收入读取路径保持合成金额一致");
        var combined=new FutuImporter(2025).Parse([trades,income]);
        Check(combined.ImportProvenance is{Count:2} rows&&rows.Any(p=>p.ReaderVersion==FutuTradeExcelImporter.TaxReaderVersion)
            &&rows.Any(p=>p.ReaderVersion=="FUTU.IncomeExcel.v1")&&rows.All(p=>p.ProjectionVersion==TaxImportProvenance.FutuAutomaticProjectionVersion&&p.Broker=="FUTU"),"交易和收入各记真实读取版本、共用税务投影版本");
        Check(combined.ImportProvenance!.All(p=>p.SourceFileHash==p.ImportBatchId&&combined.Sources.Any(s=>s.Sha256.Equals(p.SourceFileHash,StringComparison.OrdinalIgnoreCase))),"小写批次摘要与既有大写来源摘要正确绑定");
        Check(FutuPerformanceAdapter.ReaderVersion==FutuTradeExcelImporter.ReaderVersion
            && FutuPerformanceAdapter.ReaderVersion!=FutuTradeExcelImporter.TaxReaderVersion,"税务新核对口径独立于已归档绩效事实");
        if(baseline is not null)CompareBefore(baseline,[[income],[pdf],[trades,income]],Check);
        CheckAutomaticFundReconciliation(root, Check);
        CheckAutomaticAccountUpgrade(root, Check);
        Reject(()=>new FutuImporter(2025).Parse([income,pdf]),"仍拒绝重复收入主表，不能重复计税");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            try{new FutuImporter(2025).Parse([trades,income],cancellation:canceled.Token);throw new Exception("忽略取消");}
            catch(OperationCanceledException){Check(true,"版本记录不改变取消语义");}
        }
        var service=CalculationServices.Create(new Rates(),"FUTU");
        var result=service.Calculate([trades,income],2025,true,true,saveCanonicalSnapshot:true);
        var legacyShape=service.Calculate([income],2025,true,true,saveCanonicalSnapshot:true);
        Check(result.ImportProvenance!.SequenceEqual(combined.ImportProvenance!.OrderBy(p=>p.SourceFileHash,StringComparer.Ordinal)),"税务计算冻结已导入的逐原件版本");
        using var vault=new VaultManager();await vault.CreateAsync(Path.Combine(root,"vault"),"Synthetic-Futu-Provenance-2026");await TestStores.Enroll(vault);
        vault.Repository.Save(result);var loaded=vault.Repository.Load(result.SnapshotId);
        Check(loaded.ImportProvenance!.SequenceEqual(result.ImportProvenance!),"SQLCipher历史保留交易及收入版本");
        File.WriteAllText(Path.Combine(root,"exchange_rate.json"),"""{"2025":{"CNY":1,"HKD":1,"USD":1}}""");
        var workflow = new DesktopWorkflow(new DesktopOperations(vault.Repository,root,vault));
        var source = (await workflow.CalculateAsync(new([trades,pdf],2025,"FUTU",true,true,false,null,false),default)).Result;
        var plan = await workflow.PrepareHistoricalRecalculationAsync(source.SnapshotId,default);
        var missing = plan.Originals.Single(original => !original.InVault);
        var fresh = await workflow.RecalculateHistoryAsync(new(source.SnapshotId,
            new Dictionary<string,string>{{missing.Name,pdf}},null,false),default);
        Check(missing.Kind==HistoricalOriginalKind.Income && fresh.Result.RecalculatedFromSnapshotId==source.SnapshotId
            && fresh.Result.Summary==source.Summary,"富途历史新算复用库内交易，补选 PDF 收入主表");
        File.Delete(trades);File.Delete(income);
        var replay=service.Replay(loaded);
        Check(replay.ImportProvenance!.SequenceEqual(loaded.ImportProvenance!)&&replay.Summary==loaded.Summary
            &&replay.CanonicalInputDigest==loaded.CanonicalInputDigest,"无原件重放保留原版本、金额及规范输入摘要");
        var node=JsonSerializer.SerializeToNode(loaded)!.AsObject();
        node[nameof(CalculationResult.ImportProvenance)]![0]![nameof(TaxImportProvenance.ReaderVersion)]="changed";
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"修改富途读取版本破坏封存");
        node=JsonSerializer.SerializeToNode(loaded)!.AsObject();node.Remove(nameof(CalculationResult.ImportProvenance));
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"剥除富途版本依据破坏封存");
        node=JsonSerializer.SerializeToNode(legacyShape)!.AsObject();node.Remove(nameof(CalculationResult.ImportProvenance));
        node.Remove(nameof(CalculationResult.EvidenceSeal));var legacy=node.Deserialize<CalculationResult>()!;legacy.SnapshotId="FUTU-LEGACY-UNKNOWN";
        vault.Repository.Save(legacy);var old=vault.Repository.Load(legacy.SnapshotId);
        Check(old.ImportProvenance is null&&service.Replay(old).ImportProvenance is null,"缺字段旧形态及重放保持未知");
        var export=Path.Combine(root,"futu.xlsx");new ExcelReportExporter().Export(export,loaded);
        using(var stream=File.OpenRead(export))using(var book=new XSSFWorkbook(stream))
        {
            var sheet=book.GetSheet("解析版本");
            Check(sheet.LastRowNum==2&&Enumerable.Range(1,2).All(i=>sheet.GetRow(i).GetCell(4).StringCellValue==TaxImportProvenance.FutuAutomaticProjectionVersion),"底稿按两份原件披露税务投影版本");
        }
        var backup=Path.Combine(root,"futu.crsbak");await vault.BackupAsync(backup);vault.Lock();
        using var restored=new VaultManager();var target=Path.Combine(root,"restored");
        await restored.RestoreAsync(backup,target,"Synthetic-Futu-Provenance-2026",false,"Restored-Futu-Provenance-2026");
        await restored.UnlockAsync(target,"Restored-Futu-Provenance-2026");await TestStores.Authenticate(restored);
        Check(restored.Repository.Load(result.SnapshotId).ImportProvenance!.SequenceEqual(result.ImportProvenance!),"加密备份恢复保留全部富途版本依据");
        Console.WriteLine($"富途税务版本通过：{passed}项。");
    }

    private static void CheckAutomaticFundReconciliation(string root, Action<bool, string> check)
    {
        const int year = 2023;
        var activity = Path.Combine(root, "fund-activity.xlsx");
        var income = Path.Combine(root, "fund-income.xlsx");
        using (var book = new XSSFWorkbook())
        {
            WriteSheet(book, "股息利息及其他收入", ["年份", "账户号码", "账户名称", "全年股息", "全年利息", "全年其他收入", "币种"],
                ["2023", "FUND01", "合成基金账户", "0", "0", "0", "USD"]);
            using var stream = File.Create(income);
            book.Write(stream, true);
        }
        void Activity(string endingCash, string endingShares, string saleFee)
        {
            using var book = new XSSFWorkbook();
            WriteSheet(book, "账户信息", ["年份", "账户号码", "账户名称"], ["2023", "FUND01", "合成基金账户"]);
            WriteSheet(book, "交易流水",
                ["成交时间", "品类", "代码名称", "方向", "数量/面值", "价格", "币种", "账户号码", "交易所/市场", "总费用", "成交金额", "变动金额", "交收日期"],
                ["2023-02-01 10:00:00", "基金", "DEMO-FUND", "申购", "10", "10", "USD", "FUND01", "FUND", "2", "-100", "-102", "20230202"],
                ["2023-03-01 10:00:00", "基金", "DEMO-FUND", "赎回", "4", "15", "USD", "FUND01", "FUND", saleFee, "60", "59", "20230302"]);
            WriteSheet(book, "持仓总览", ["时期类型", "日期", "品类", "账户号码", "代码名称", "交易所/市场", "币种", "数量/面值", "市值"],
                ["期初", "20221230", "基金", "FUND01", "DEMO-FUND", "FUND", "USD", "0", ""],
                ["期末", "20231229", "基金", "FUND01", "DEMO-FUND", "FUND", "USD", endingShares, "90"]);
            WriteSheet(book, "资金总览", ["时期类型", "日期", "账户号码", "币种", "金额"],
                ["期初", "20221230", "FUND01", "USD", "1000"],
                ["期末", "20231229", "FUND01", "USD", endingCash]);
            WriteSheet(book, "资金进出", ["日期", "账户号码", "类型", "方向", "币种", "变动金额", "备注"]);
            using var stream = File.Create(activity);
            book.Write(stream, true);
        }
        var calculator = CalculationServices.Create(new Rates(), "FUTU", year);
        Activity("957", "6", "1");
        var complete = calculator.Calculate([activity, income], year, false, false, saveCanonicalSnapshot: true);
        check(complete is { Complete: true, ScopeConfirmed: true, ReconciliationStatus: ReconciliationStatus.Matched } &&
            complete.Matches.Single().Gain == 18.20m && complete.TradeAmountChecks.Count == 2 &&
            complete.CashChecks.Single().Status == "一致" && calculator.Replay(complete).Complete &&
            ResultLabels.Usage(complete) == "自动核对通过",
            "无需手动确认，自动核对账户范围、基金申赎、现金和周末年末份额，同构建可重放");
        Activity("", "6", "1");
        var missingCash = calculator.Calculate([activity, income], year, true, true);
        check(!missingCash.Complete && missingCash.CashChecks.Single().ReportedEnding is null,
            "现金缺值保留缺口");
        Activity("957", "7", "1");
        var positionMismatch = calculator.Calculate([activity, income], year, true, true);
        check(!positionMismatch.Complete && positionMismatch.Issues.Any(i => i.Code == "POSITION_MISMATCH"),
            "期末份额不一致自动定位");
        Activity("957", "6", "");
        var missingFee = calculator.Calculate([activity, income], year, true, true);
        check(!missingFee.Complete && missingFee.Matches.Count == 0 && missingFee.Issues.Any(i => i.Code == "FUND_TRADE_EVIDENCE"),
            "基金成交缺费用时保留证据缺口");
    }

    private static void CheckAutomaticAccountUpgrade(string root, Action<bool, string> check)
    {
        var activity = Path.Combine(root, "upgrade-activity.xlsx");
        var income = Path.Combine(root, "upgrade-income.xlsx");
        using (var book = new XSSFWorkbook())
        {
            WriteSheet(book, "股息利息及其他收入", ["年份", "账户名称", "全年股息", "全年利息", "全年其他收入", "币种"],
                ["2025", "港股融資帳戶", "0", "0", "0", "USD"], ["2025", "保證金綜合帳戶-證券", "0", "0", "0", "USD"]);
            using var stream = File.Create(income);
            book.Write(stream, true);
        }
        void Activity(string outgoingQuantity)
        {
            using var book = new XSSFWorkbook();
            WriteSheet(book, "账户信息", ["年份", "账户号码", "账户名称"],
                ["2025", "DEMO0101", "港股孖展帳戶(0101)"], ["2025", "DEMO0202", "保證金綜合帳戶(0202) - 證券"]);
            WriteSheet(book, "交易流水",
                ["成交时间", "品类", "代码名称", "方向", "数量/面值", "价格", "币种", "账户号码", "交易所/市场", "总费用", "成交金额", "变动金额"],
                ["2025-02-01 10:00:00", "基金", "DEMO-FUND", "申购", "10", "10", "USD", "DEMO0101", "FUND", "2", "-100", "-102"],
                ["2025-03-01 10:00:00", "基金", "DEMO-FUND", "赎回", "4", "15", "USD", "DEMO0202", "FUND", "1", "60", "59"]);
            WriteSheet(book, "持仓总览", ["时期类型", "日期", "品类", "账户号码", "代码名称", "交易所/市场", "币种", "数量/面值"],
                ["期初", "20241231", "基金", "DEMO0101", "DEMO-FUND", "FUND", "USD", "0"],
                ["期初", "20241231", "基金", "DEMO0202", "DEMO-FUND", "FUND", "USD", "0"],
                ["期末", "20251231", "基金", "DEMO0202", "DEMO-FUND", "FUND", "USD", "6"]);
            WriteSheet(book, "资产进出", ["日期", "账户号码", "代码名称", "交易所/市场", "币种", "数量", "方向", "类型", "备注"],
                ["20250215", "DEMO0101", "DEMO-FUND", "FUND", "USD", outgoingQuantity, "Out", "资产进出", ""],
                ["20250215", "DEMO0202", "DEMO-FUND", "FUND", "USD", "10", "In", "账户升级", ""]);
            WriteSheet(book, "资金总览", ["时期类型", "日期", "账户号码", "币种", "金额"],
                ["期初", "20241231", "DEMO0101", "USD", "1000"], ["期末", "20251231", "DEMO0101", "USD", "898"],
                ["期初", "20241231", "DEMO0202", "USD", "0"], ["期末", "20251231", "DEMO0202", "USD", "59"]);
            WriteSheet(book, "资金进出", ["日期", "账户号码", "类型", "方向", "币种", "变动金额", "备注"]);
            using var stream = File.Create(activity);
            book.Write(stream, true);
        }

        var calculator = CalculationServices.Create(new Rates(), "FUTU");
        Activity("-10");
        var complete = calculator.Calculate([activity, income], 2025, false, true, saveCanonicalSnapshot: true);
        check(complete.Complete && complete.CoveredAccounts.Count == 2 &&
            complete.Matches.Single() is { Cost: 40.80m, Gain: 18.20m, Key.Account: "FUTU:DEMO0202" } &&
            calculator.Replay(complete).Complete,
            "收入账户按券商名称唯一绑定，等量升级自动沿用原买入成本");
        Activity("-9");
        var mismatch = calculator.Calculate([activity, income], 2025, false, true);
        check(!mismatch.Complete && mismatch.Matches.Count == 0 && mismatch.Issues.Any(i => i.Code == "UNSUPPORTED_TRANSFER"),
            "升级数量无法配对时自动阻断相关成本");
    }

    private static void WriteSheet(XSSFWorkbook book, string name, params string[][] rows)
    {
        var sheet = book.CreateSheet(name);
        for (var r = 0; r < rows.Length; r++)
        {
            var row = sheet.CreateRow(r);
            for (var c = 0; c < rows[r].Length; c++) row.CreateCell(c).SetCellValue(rows[r][c]);
        }
    }
    private static void CompareBefore(string baseline,string[][] cases,Action<bool,string> check)
    {
        var context=new AssemblyLoadContext("FutuTaxBefore",isCollectible:true);
        try
        {
            var type=context.LoadFromAssemblyPath(Path.GetFullPath(baseline)).GetType("CRS.Infrastructure.FutuImporter",true)!;
            foreach(var files in cases)
            {
                var before=(ImportData)type.GetMethod("Parse")!.Invoke(Activator.CreateInstance(type,[2025]),[files,null,CancellationToken.None])!;
                var after=new FutuImporter(2025).Parse(files);
                var a=JsonSerializer.SerializeToNode(before)!.AsObject();var b=JsonSerializer.SerializeToNode(after)!.AsObject();
                a.Remove(nameof(ImportData.ImportProvenance));b.Remove(nameof(ImportData.ImportProvenance));
                foreach (var item in new[] { a, b })
                {
                    item.Remove(nameof(ImportData.Warnings));
                    foreach (var cash in item[nameof(ImportData.CashChecks)]!.AsArray())
                        cash!.AsObject().Remove(nameof(CashReconciliation.Reason));
                }
                check(a.ToJsonString()==b.ToJsonString(),string.Join("+",files.Select(Path.GetFileName))+"既有样本的原始金额与旧构建一致");
            }
        }
        finally{context.Unload();}
    }
    private static void WriteIncome(string path)
    {
        using var book=new XSSFWorkbook();var sheet=book.CreateSheet("股息利息及其他收入");
        string[][] rows=[["年份","账户号码","账户名称","全年股息","全年利息","全年其他收入","币种"],["2025","SYNTHETIC001","SYNTHETIC","100","5","0","USD"]];
        for(var r=0;r<rows.Length;r++){var row=sheet.CreateRow(r);for(var c=0;c<rows[r].Length;c++)row.CreateCell(c).SetCellValue(rows[r][c]);}
        using var output=File.Create(path);book.Write(output,true);
    }
    private static void WritePdf(string path)
    {
        var builder=new PdfDocumentBuilder();var font=builder.AddStandard14Font(Standard14Font.Helvetica);var page=builder.AddPage(650,800);
        page.AddText("2025 Annual Report",12,new PdfPoint(10,750),font);
        string[] headers=["Account","Dividends","Interest","Other income"],values=["SYNTHETIC","USD 100.00","USD 5.00","USD 0.00"];
        for(var i=0;i<headers.Length;i++){page.AddText(headers[i],10,new PdfPoint(10+i*145,650),font);page.AddText(values[i],10,new PdfPoint(10+i*145,630),font);}
        File.WriteAllBytes(path,builder.Build());
    }
    private sealed class Rates:IExchangeRateProvider
    {
        public string Fingerprint=>"synthetic-futu-provenance";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);
    }
}
