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
        Check(combined.ImportProvenance is{Count:2} rows&&rows.Any(p=>p.ReaderVersion=="FutuAnnualActivity.v1")
            &&rows.Any(p=>p.ReaderVersion=="FUTU.IncomeExcel.v1")&&rows.All(p=>p.ProjectionVersion=="FUTU.Tax.v1"&&p.Broker=="FUTU"),"交易和收入各记真实读取版本、共用税务投影版本");
        Check(combined.ImportProvenance!.All(p=>p.SourceFileHash==p.ImportBatchId&&combined.Sources.Any(s=>s.Sha256.Equals(p.SourceFileHash,StringComparison.OrdinalIgnoreCase))),"小写批次摘要与既有大写来源摘要正确绑定");
        Check(FutuPerformanceAdapter.ReaderVersion==FutuTradeExcelImporter.ReaderVersion,"税务与绩效引用同一年度交易读取版本");
        if(baseline is not null)CompareBefore(baseline,[[income],[pdf],[trades,income]],Check);
        Reject(()=>new FutuImporter(2025).Parse([income,pdf]),"仍拒绝重复收入主表，不能重复计税");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            try{new FutuImporter(2025).Parse([trades,income],cancellation:canceled.Token);throw new Exception("忽略取消");}
            catch(OperationCanceledException){Check(true,"版本记录不改变取消语义");}
        }
        var service=CalculationServices.Create(new Rates(),"FUTU");
        var result=service.Calculate([trades,income],2025,true,true,saveCanonicalSnapshot:true);
        Check(result.ImportProvenance!.SequenceEqual(combined.ImportProvenance!.OrderBy(p=>p.SourceFileHash,StringComparer.Ordinal)),"税务计算冻结已导入的逐原件版本");
        using var vault=new VaultManager();await vault.CreateAsync(Path.Combine(root,"vault"),"Synthetic-Futu-Provenance-2026");await TestStores.Enroll(vault);
        vault.Repository.Save(result);var loaded=vault.Repository.Load(result.SnapshotId);
        Check(loaded.ImportProvenance!.SequenceEqual(result.ImportProvenance!),"SQLCipher历史保留交易及收入版本");
        File.Delete(trades);File.Delete(income);
        var replay=service.Replay(loaded);
        Check(replay.ImportProvenance!.SequenceEqual(loaded.ImportProvenance!)&&replay.Summary==loaded.Summary
            &&replay.CanonicalInputDigest==loaded.CanonicalInputDigest,"无原件重放保留原版本、金额及规范输入摘要");
        var node=JsonSerializer.SerializeToNode(loaded)!.AsObject();
        node[nameof(CalculationResult.ImportProvenance)]![0]![nameof(TaxImportProvenance.ReaderVersion)]="changed";
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"修改富途读取版本破坏封存");
        node=JsonSerializer.SerializeToNode(loaded)!.AsObject();node.Remove(nameof(CalculationResult.ImportProvenance));
        Reject(()=>RunEvidenceSeal.Verify(node.Deserialize<CalculationResult>()!),"剥除富途版本依据破坏封存");
        node.Remove(nameof(CalculationResult.EvidenceSeal));var legacy=node.Deserialize<CalculationResult>()!;legacy.SnapshotId="FUTU-LEGACY-UNKNOWN";
        vault.Repository.Save(legacy);var old=vault.Repository.Load(legacy.SnapshotId);
        Check(old.ImportProvenance is null&&service.Replay(old).ImportProvenance is null,"缺字段旧形态及重放保持未知");
        var export=Path.Combine(root,"futu.xlsx");new ExcelReportExporter().Export(export,loaded);
        using(var stream=File.OpenRead(export))using(var book=new XSSFWorkbook(stream))
        {
            var sheet=book.GetSheet("解析版本");
            Check(sheet.LastRowNum==2&&Enumerable.Range(1,2).All(i=>sheet.GetRow(i).GetCell(4).StringCellValue=="FUTU.Tax.v1"),"底稿按两份原件披露税务投影版本");
        }
        var backup=Path.Combine(root,"futu.crsbak");await vault.BackupAsync(backup);vault.Lock();
        using var restored=new VaultManager();var target=Path.Combine(root,"restored");
        await restored.RestoreAsync(backup,target,"Synthetic-Futu-Provenance-2026",false,"Restored-Futu-Provenance-2026");
        await restored.UnlockAsync(target,"Restored-Futu-Provenance-2026");await TestStores.Authenticate(restored);
        Check(restored.Repository.Load(result.SnapshotId).ImportProvenance!.SequenceEqual(result.ImportProvenance!),"加密备份恢复保留全部富途版本依据");
        Console.WriteLine($"富途税务版本通过：{passed}项。");
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
                check(a.ToJsonString()==b.ToJsonString(),string.Join("+",files.Select(Path.GetFileName))+"除版本审计外与旧构建逐字段一致（非独立金额签认）");
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
