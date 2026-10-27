using System.Globalization;
using System.Runtime.Loader;
using System.Text.Json;
using CRS.Documents.Excel;
using CRS.Domain;
using CRS.Infrastructure;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

/// <summary>文档层使用实际XLS/XLSX检查值、限额与取消；业务迁移另与保留的旧程序集比较。</summary>
internal static class ExcelDocumentChecks
{
    internal static void Run(string root,string? baseline=null)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS Excel文档："+label);}
        void Reject<T>(Action action,string label) where T:Exception
        {try{action();}catch(T){Check(true,label);return;}throw new Exception(label+"未拒绝。");}
        foreach(var xlsx in new[]{false,true})
        {
            using IWorkbook book=xlsx?new XSSFWorkbook():new HSSFWorkbook();
            var sheet=book.CreateSheet("原始值");sheet.CreateRow(0).CreateCell(0).SetCellValue("列名");
            var row=sheet.CreateRow(4);row.CreateCell(0).SetCellValue(0.123456789);
            var format=book.CreateCellStyle();format.DataFormat=book.CreateDataFormat().GetFormat("0.00");row.GetCell(0).CellStyle=format;
            row.CreateCell(2).SetCellValue(new DateTime(2025,10,27,13,24,35));var date=book.CreateCellStyle();date.DataFormat=book.CreateDataFormat().GetFormat("yyyy-mm-dd hh:mm:ss");row.GetCell(2).CellStyle=date;
            row.CreateCell(3).SetCellFormula("1+2");row.CreateCell(4).SetCellErrorValue(7);row.CreateCell(5).SetCellValue(true);
            using var bytes=new MemoryStream();book.Write(bytes,true);bytes.Position=0;
            using(var reader=ExcelReader.Open(bytes))
            {
                var rows=reader.ReadRows("原始值");var actual=rows[1];
                Check(reader.SheetNames.SequenceEqual(["原始值"])&&rows.Count==2&&actual.Number==5,"XLS/XLSX保留物理行号及工作表名："+xlsx);
                Check(actual.GetCell(0) is {Kind:ExcelCellKind.Number,RawText:"0.123456789",DisplayText:"0.12"},"原始数值不受显示舍入影响："+xlsx);
                Check(actual.GetCell(1) is {Kind:ExcelCellKind.Blank,RawText:""}&&actual.GetCell(20) is null,"缺失列保持空值："+xlsx);
                Check(actual.GetCell(2) is {Kind:ExcelCellKind.Date,RawText:"2025-10-27 13:24:35"},"日期不丢秒且不依赖机器时区："+xlsx);
                Check(actual.GetCell(3) is {Kind:ExcelCellKind.Formula,RawText:"1+2"}&&actual.GetCell(4)?.Kind==ExcelCellKind.Error
                    &&actual.GetCell(5)?.Kind==ExcelCellKind.Boolean,"公式及错误只返回类型和原文，不执行："+xlsx);
                Reject<InvalidDataException>(()=>reader.ReadRows("不存在"),"缺表明确失败："+xlsx);
            }
            Check(bytes.CanRead,"读取器不关闭调用方流："+xlsx);
        }
        using var writer=new ExcelWriter();writer.AddSheet("共用输出",["账户","金额","缺失","零值","计数"],
            [["=HYPERLINK(\"https://invalid.example\")",1.23456789m,null,0m,7]],new(true,"0.00########",[6000,12000,6000,6000,6000]));
        writer.AddSheet("绩效选项",["计数"],[[7]],new(IntegersAsNumbers:false,FreezeHeader:false));
        using var output=new MemoryStream();writer.Write(output);Check(output.CanWrite,"写入器保持调用方流打开");output.Position=0;
        using(var book=WorkbookFactory.Create(output))
        {
            var sheet=book.GetSheet("共用输出");var row=sheet.GetRow(1);
            Check(row.GetCell(0).CellType==CellType.String&&row.GetCell(0).StringCellValue.StartsWith('='),"等号前缀按文字输出");
            Check(row.GetCell(1).CellType==CellType.Numeric&&row.GetCell(1).NumericCellValue==1.23456789
                &&row.GetCell(1).CellStyle.GetDataFormatString()=="0.00########","小数数值及金额显示格式保留");
            Check(row.GetCell(2).CellType==CellType.String&&row.GetCell(2).StringCellValue==""&&row.GetCell(3).CellType==CellType.Numeric
                &&row.GetCell(3).NumericCellValue==0&&row.GetCell(4).NumericCellValue==7,"空值、真实零及整数区分");
            Check(book.GetFontAt(sheet.GetRow(0).GetCell(0).CellStyle.FontIndex).IsBold&&sheet.GetColumnWidth(1)==12000
                &&sheet.PaneInformation is {IsFreezePane:true,HorizontalSplitPosition:1},"表头、列宽及冻结行保留");
            Check(book.GetSheet("绩效选项").GetRow(1).GetCell(0).CellType==CellType.String
                &&book.GetSheet("绩效选项").PaneInformation is null,"可保留绩效原整数文本及无冻结选项");
        }
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Reject<OperationCanceledException>(()=>ExcelReader.Open([1,2,3],cancelled.Token),"预取消读取");
        Reject<OperationCanceledException>(()=>writer.Write(new MemoryStream(),cancelled.Token),"预取消写入");
        using var during=new CancellationTokenSource();
        IEnumerable<object?[]> Rows(){yield return [1];during.Cancel();yield return [2];}
        Reject<OperationCanceledException>(()=>writer.AddSheet("取消",["值"],Rows(),cancellation:during.Token),"构建行时响应取消");
        Reject<InvalidDataException>(()=>ExcelReader.Open([]),"空文件拒绝");
        Reject<InvalidDataException>(()=>ExcelReader.Open([1,2,3]),"损坏格式拒绝");
        Reject<InvalidDataException>(()=>ExcelReader.Open(new byte[ExcelReader.MaxFileBytes+1]),"输入容量拒绝");
        Limit(ExcelReader.MaxRowIndex+1,0,"物理行限额");Limit(0,ExcelReader.MaxColumns,"列限额");
        void Limit(int row,int column,string label)
        {
            using var book=new XSSFWorkbook();book.CreateSheet("限额").CreateRow(row).CreateCell(column).SetCellValue("值");
            using var bytes=new MemoryStream();book.Write(bytes,true);bytes.Position=0;using var reader=ExcelReader.Open(bytes);
            Reject<InvalidDataException>(()=>reader.ReadRows("限额"),label);
        }
        Check(typeof(ExcelReader).Assembly.GetReferencedAssemblies().All(x=>!x.Name!.StartsWith("CRS.")),"文档程序集不引用CRS业务、宿主或安全模块");
        if(baseline is not null)CompareExports(root,baseline,Check);
        Console.WriteLine($"Excel文档通过：{passed}项。");
    }
    private static void CompareExports(string root,string baseline,Action<bool,string> check)
    {
        var context=new AssemblyLoadContext("ExcelBefore",isCollectible:true);
        try
        {
            var method=context.LoadFromAssemblyPath(Path.GetFullPath(baseline)).GetType("CRS.Infrastructure.ExcelReports",true)!.GetMethod("Export")!;
            var cases=new[]{new CalculationResult{Year=2025,Broker="FUTU",SnapshotId="SYNTHETIC-EXCEL-UNKNOWN",Summary=new(10,0,0,2,0,0,2),UnknownGainCount=1,Warnings=["=1+2"]},
                new CalculationResult{Year=2025,SnapshotId="SYNTHETIC-EXCEL-ZERO",Summary=new(0,0,0,0,0,0,0),EstimatedTopUpCny=0,Reconciled=true,
                    CalculationStatus=CalculationStatus.Completed,DataCompleteness=DataCompleteness.Confirmed,ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady}};
            foreach(var result in cases)
            {
                var before=Path.Combine(root,result.SnapshotId+"-before.xlsx");var after=Path.Combine(root,result.SnapshotId+"-after.xlsx");
                ReviewNote[] notes=[new(result.SnapshotId,"合成复核","=1+2","synthetic")];
                method.Invoke(null,[before,result,notes,CancellationToken.None,null]);ExcelReports.Export(after,result,notes);
                check(Signature(before)==Signature(after),"迁移前后底稿逐单元格值/类型/格式/表头/列宽/冻结行一致："+result.SnapshotId);
            }
        }
        finally{context.Unload();}
    }
    private static string Signature(string path)
    {
        using var stream=File.OpenRead(path);using var book=WorkbookFactory.Create(stream);
        return JsonSerializer.Serialize(Enumerable.Range(0,book.NumberOfSheets).Select(s=>
        {
            var sheet=book.GetSheetAt(s);var pane=sheet.PaneInformation;
            return new{sheet.SheetName,Freeze=pane is null?"":$"{pane.IsFreezePane}/{pane.HorizontalSplitPosition}/{pane.VerticalSplitPosition}",
                Widths=Enumerable.Range(0,sheet.GetRow(0).LastCellNum).Select(sheet.GetColumnWidth).ToArray(),Rows=sheet.Cast<IRow>().Select(r=>new{r.RowNum,
                    Cells=r.Cells.Select(c=>new{c.ColumnIndex,Type=c.CellType.ToString(),Value=c.CellType==CellType.Numeric?c.NumericCellValue.ToString("R",CultureInfo.InvariantCulture):c.ToString(),
                        Format=c.CellStyle.GetDataFormatString(),Bold=book.GetFontAt(c.CellStyle.FontIndex).IsBold}).ToArray()}).ToArray()};
        }).ToArray());
    }
}
