using System.Text.Json;
using CRS.Domain.Performance;
using CRS.Infrastructure;
using NPOI.XSSF.UserModel;

internal static class PerformanceImportRegression
{
    private const string Folder="tests/fixtures";
    internal static void Run(bool capture=false)
    {
        var futu=Path.Combine(Folder,"futu_performance.xlsx");
        if(capture && !File.Exists(futu))CreateFutu(futu);
        var results=new Dictionary<string,PerformanceData>();
        foreach(var name in new[]{"ibkr_activity.xml","ibkr_reconciliation.xml","futu_performance.xlsx"})
        {
            var input=FrozenInput.Read(Path.Combine(Folder,name));
            var parsed=name.EndsWith("xml",StringComparison.Ordinal)?PerformanceBrokerImporter.Ibkr(input,default):PerformanceBrokerImporter.Futu(input,default);
            results.Add(name,parsed);
            var sources=parsed.Events.Select(e=>e.Source).Concat(parsed.Valuations.Select(v=>v.Source)).Concat(parsed.Prices.Select(p=>p.Source)).Concat(parsed.Fx.Select(f=>f.Source)).Concat(parsed.Coverage.Select(c=>c.Source));
            if(sources.Any(s=>s.DocumentId!=input.Sha256 || string.IsNullOrEmpty(s.RowIdentity)))throw new Exception("券商事实缺少冻结来源及行定位。");
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            try {if(name.EndsWith("xml",StringComparison.Ordinal))PerformanceBrokerImporter.Ibkr(input,canceled.Token);else PerformanceBrokerImporter.Futu(input,canceled.Token);throw new Exception("券商解析忽略取消。");}
            catch(OperationCanceledException){}
        }
        var json=JsonSerializer.Serialize(results);var path=Path.Combine(Folder,"performance-imports-v1.json");
        if(capture)File.WriteAllText(path,json);
        else if(File.ReadAllText(path)!=json)throw new Exception("券商冻结事实出现逐字段差异，请独立审查解析变化。");
        Console.WriteLine("券商冻结回归通过：IBKR/FUTU 完整事实、来源和取消边界。");
    }
    private static void CreateFutu(string path)
    {
        using var book=new XSSFWorkbook();
        void Sheet(string name,string[] headers,params string[][] rows)
        {
            var sheet=book.CreateSheet(name);var header=sheet.CreateRow(0);
            for(var i=0;i<headers.Length;i++)header.CreateCell(i).SetCellValue(headers[i]);
            for(var r=0;r<rows.Length;r++){var row=sheet.CreateRow(r+1);for(var c=0;c<rows[r].Length;c++)row.CreateCell(c).SetCellValue(rows[r][c]);}
        }
        Sheet("账户信息",["年份","账户号码","账户名称"],["2025","SYNTHETIC001","合成账户"]);
        Sheet("交易流水",["成交时间","品类","代码名称","方向","数量/面值","价格","币种","账户号码","交易所/市场","总费用","成交金额","变动金额"],
            ["2025-01-02 10:00:00","证券","DEMO","买入","10","100","USD","SYNTHETIC001","US","2","-1000","-1002"]);
        Sheet("资金进出",["日期","账户号码","类型","方向","币种","变动金额","备注"],
            ["2025-02-01","SYNTHETIC001","入金","入","USD","500","合成入金"],
            ["2025-03-01","SYNTHETIC001","股息","入","USD","20","DEMO"]);
        Sheet("持仓总览",["时期类型","日期","品类","账户号码","代码名称","交易所/市场","币种","数量/面值"],
            ["期初","2024-12-31","证券","SYNTHETIC001","DEMO","US","USD","0"],
            ["期末","2025-12-31","证券","SYNTHETIC001","DEMO","US","USD","10"]);
        Sheet("资金总览",["时期类型","日期","账户号码","币种","金额"],
            ["期初","2024-12-31","SYNTHETIC001","USD","2000"],
            ["期末","2025-12-31","SYNTHETIC001","USD","1518"]);
        using var file=File.Create(path);book.Write(file,true);
    }
}
