using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using CRS.Infrastructure.Pdf;
using NPOI.XSSF.UserModel;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class ImportConsistencyChecks
{
    /// <summary>解析后替换／删除原件不能改变快照摘要，XML、Excel、PDF 共用冻结内容。</summary>
    internal static void Run(string root)
    {
        var directory=Path.Combine(root,"input-consistency"); Directory.CreateDirectory(directory);
        var activity=Path.Combine(directory,"activity.xml"); var opening=Path.Combine(directory,"opening.xml");
        File.WriteAllText(activity,Xml("ORIGINAL",2025)); File.WriteAllText(opening,Xml("ORIGINAL",2024));
        var activityHash=Hash(File.ReadAllBytes(activity)); var openingHash=Hash(File.ReadAllBytes(opening));
        var importer=new AfterParse(new IbkrImporter(),()=> {File.WriteAllText(activity,Xml("CHANGED",2025)); File.Delete(opening);});
        var service=Service(importer);
        var result=service.Calculate([activity],2025,false,true,openingXml:opening,saveCanonicalSnapshot:true);
        Assert(result.CoveredAccounts.SequenceEqual(["ORIGINAL"]),"解析结果被后来版本替换。");
        AssertReferences(result,activityHash,openingHash);
        Reject(()=>service.VerifyOriginalFiles(result,[activity],opening));
        File.Delete(activity); var replay=service.Replay(result);
        Assert(replay.CanonicalInputDigest==result.CanonicalInputDigest && replay.Summary==result.Summary,"完整快照复算依赖已删除原件。");
        // 冻结副本不会对调用者公开可写数组；原路径替换和删除不改变读取结果。
        var xlsx=Path.Combine(directory,"income.xlsx"); WriteIncome(xlsx);
        var frozen=FrozenInput.Read(xlsx); var incomeHash=frozen.Sha256;
        File.WriteAllText(xlsx,"replaced-original");
        var exposed=frozen.CopyBytes(); exposed[0]^=1;
        using(var stream=frozen.OpenRead()) Assert(!stream.CanWrite,"冻结输入暴露了可写流。");
        var rows=FutuDividendExcelImporter.Read(frozen);
        Assert(rows.Single()["全年股息"]=="100" && frozen.Sha256==incomeHash,"Excel 没有读取冻结内容或副本修改污染原数据。");
        WriteIncome(xlsx);
        var futu=Service(new AfterParse(new FutuImporter(2025),()=>File.Delete(xlsx)));
        var futuResult=futu.Calculate([xlsx],2025,true,true,saveCanonicalSnapshot:true);
        Assert(futuResult.Summary.DividendCny==100 && futuResult.Summary.InterestCny==5,"富途冻结收入金额错误。");
        AssertReferences(futuResult,futuResult.Sources.Single().Sha256.ToLowerInvariant(),null);
        // 合成文字 PDF 使用现有受支持布局，删除原路径后仍能解析同一份内容。
        var pdf=Path.Combine(directory,"income.pdf"); WritePdf(pdf);
        var pdfInput=FrozenInput.Read(pdf); var pdfHash=pdfInput.Sha256; File.Delete(pdf);
        Assert(FutuDividendPdfImporter.Read(pdfInput,default).Single()["Dividends"].Contains("100.00")
            && Hash(pdfInput.CopyBytes())==pdfHash,"PDF 文本与摘要没有绑定同一副本。");
        var empty=Path.Combine(directory,"empty"); File.WriteAllBytes(empty,[]); Reject(()=>FrozenInput.Read(empty));
        var large=Path.Combine(directory,"large"); using(var output=File.Create(large)) output.SetLength(25*1024*1024+1);
        Reject(()=>FrozenInput.Read(large));
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        try {FrozenInput.Read(empty,cancellation.Token); throw new Exception("取消仍读取原件。");} catch(OperationCanceledException) { }
        // 同名不同目录及解析器返回冲突来源均不能产生可持久化结果。
        Reject(()=>Service(new IbkrImporter()).Calculate(["a/report.xml","b/report.xml"],2025,true,true));
        Reject(()=>Service(new BrokenImporter(false)).Calculate(["report.xml"],2025,true,true));
        Reject(()=>Service(new BrokenImporter(true)).Calculate(["report.xml"],2025,true,true));
        Console.WriteLine("导入一致性通过：XML／期初替换删除、无原件复算、只读副本、Excel／PDF 同源、大小及取消限制、同名和缺失／冲突摘要拒绝。");
    }
    private static string Xml(string account,int year)=>$"<FlexQueryResponse type=\"AF\"><FlexStatements><FlexStatement accountId=\"{account}\" fromDate=\"{year}0101\" toDate=\"{year}1231\"><Trades/><CashTransactions/><OpenPositions/></FlexStatement></FlexStatements></FlexQueryResponse>";
    private static CalculationService Service(IBrokerImporter importer)=>new(new Rates(),importer,new CarryValidator(),new FileCalculationEvidence());
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void AssertReferences(CalculationResult result,string activityHash,string? openingHash)
    {
        using var document=JsonDocument.Parse(result.CanonicalInputJson); var root=document.RootElement;
        Assert(root.GetProperty("files")[0].GetProperty("sha256").GetString()==activityHash,"快照活动指纹没有采用解析时字节。");
        Assert(result.Sources.Where(s=>!s.Opening).All(s=>s.Sha256.Equals(activityHash,StringComparison.OrdinalIgnoreCase)),"来源与输入引用摘要不一致。");
        if(openingHash is not null) Assert(root.GetProperty("opening").GetProperty("sha256").GetString()==openingHash,"期初摘要重新读取了原件。");
        else Assert(root.GetProperty("opening").ValueKind==JsonValueKind.Null,"未选择期初却产生期初摘要。");
    }
    private static void WriteIncome(string path)
    {
        using var workbook=new XSSFWorkbook(); var sheet=workbook.CreateSheet("股息利息及其他收入");
        string[][] rows=[["年份","账户名称","全年股息","全年利息","全年其他收入","币种"],["2025","SYNTHETIC","100","5","0","USD"]];
        for(var r=0;r<rows.Length;r++) {var row=sheet.CreateRow(r); for(var c=0;c<rows[r].Length;c++) row.CreateCell(c).SetCellValue(rows[r][c]);}
        using var output=File.Create(path); workbook.Write(output,true);
    }
    private static void WritePdf(string path)
    {
        var builder=new PdfDocumentBuilder(); var font=builder.AddStandard14Font(Standard14Font.Helvetica); var page=builder.AddPage(650,800);
        page.AddText("2025 Annual Report",12,new PdfPoint(10,750),font);
        string[] titles=["Account","Dividends","Interest","Other income"],values=["SYNTHETIC","USD 100.00","USD 5.00","USD 0.00"];
        for(var i=0;i<titles.Length;i++) {page.AddText(titles[i],10,new PdfPoint(10+i*145,650),font); page.AddText(values[i],10,new PdfPoint(10+i*145,630),font);}
        File.WriteAllBytes(path,builder.Build());
    }
    private static void Assert(bool success,string message) {if(!success) throw new Exception(message);}
    private static void Reject(Action action) {try {action();} catch(CrsException) {return;} throw new Exception("无效输入未被拒绝。");}
    private sealed class AfterParse(IBrokerImporter inner,Action changed):IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)
        {var data=inner.Parse(files,opening,cancellationToken); changed(); return data;}
    }
    private sealed class Rates:IExchangeRateProvider
    {
        public string Fingerprint=>"synthetic";
        public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);
    }
    private sealed class BrokenImporter(bool conflicting):IBrokerImporter
    {
        public ImportData Parse(IEnumerable<string> files,string? opening=null,CancellationToken cancellationToken=default)
        {
            var data=new ImportData(); data.Sources.Add(new("report.xml",conflicting?"one":"","ACCOUNT",new(2025,1,1),new(2025,12,31),true,true,true));
            if(conflicting) data.Sources.Add(data.Sources[0] with {Sha256="two"});
            return data;
        }
    }
}
