using System.Globalization;
using NPOI.SS.UserModel;
namespace CRS.Documents.Excel;

/// <summary>共用的XLS/XLSX读取入口；保存原始行号、数值精度和显示文本，不执行公式、宏或外部链接。</summary>
public sealed class ExcelReader : IDisposable
{
    public const int MaxFileBytes=25*1024*1024;
    public const int MaxRowIndex=100000;
    public const int MaxColumns=1000;
    private readonly IWorkbook workbook;
    private readonly DataFormatter formatter=new(CultureInfo.InvariantCulture);
    private bool disposed;
    private ExcelReader(IWorkbook workbook)=>this.workbook=workbook;
    public IReadOnlyList<string> SheetNames
    {
        get{ObjectDisposedException.ThrowIf(disposed,this);return Array.AsReadOnly(Enumerable.Range(0,workbook.NumberOfSheets).Select(workbook.GetSheetName).ToArray());}
    }
    public static ExcelReader Open(string path,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Open(stream,cancellation);
    }
    public static ExcelReader Open(byte[] content,CancellationToken cancellation=default)
    {using var stream=new MemoryStream(content,false);return Open(stream,cancellation);}
    /// <summary>复制有界输入后打开工作簿；不关闭调用方流。NPOI构建期间在调用前后检查取消。</summary>
    public static ExcelReader Open(Stream input,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(input.CanSeek&&input.Length-input.Position>MaxFileBytes)throw new InvalidDataException("Excel 文件不能超过25 MiB。");
        using var frozen=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=input.Read(buffer))>0)
        {
            cancellation.ThrowIfCancellationRequested();
            if(frozen.Length+read>MaxFileBytes)throw new InvalidDataException("Excel 文件不能超过25 MiB。");
            frozen.Write(buffer,0,read);
        }
        if(frozen.Length==0)throw new InvalidDataException("Excel 文件不能为空。");
        cancellation.ThrowIfCancellationRequested();frozen.Position=0;IWorkbook? book=null;
        try
        {
            book=WorkbookFactory.Create(frozen);cancellation.ThrowIfCancellationRequested();return new(book);
        }
        catch(OperationCanceledException){book?.Dispose();throw;}
        catch(Exception error){book?.Dispose();throw new InvalidDataException("无法读取Excel，请检查文件是否损坏、加密或格式不受支持。",error);}
    }
    public IReadOnlyList<ExcelRow> ReadRows(string sheetName,CancellationToken cancellation=default)
        =>EnumerateRows(sheetName,cancellation).ToList().AsReadOnly();
    /// <summary>按物理行顺序读取，调用方可先校验表头再处理数据，不额外保留整表副本。</summary>
    public IEnumerable<ExcelRow> EnumerateRows(string sheetName,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();
        var sheet=workbook.GetSheet(sheetName)??throw new InvalidDataException("Excel 中没有指定工作表。");
        if(sheet.LastRowNum>MaxRowIndex)throw new InvalidDataException("Excel 工作表不能超过100000个数据行。");
        foreach(IRow row in sheet)
        {
            cancellation.ThrowIfCancellationRequested();
            if(row.LastCellNum>MaxColumns)throw new InvalidDataException("Excel 工作表列数不能超过1000。");
            var cells=new ExcelCell[Math.Max(0,(int)row.LastCellNum)];
            for(var c=0;c<cells.Length;c++){cancellation.ThrowIfCancellationRequested();cells[c]=ReadCell(row.GetCell(c),c);}
            yield return new(row.RowNum+1,Array.AsReadOnly(cells));
        }
    }
    /// <summary>方便读取表格显示文本；公式只返回其表达式，不读取或执行外部工作簿。</summary>
    public static List<List<string>> ReadSheet(string path,string sheetName,CancellationToken cancellation=default)
    {using var reader=Open(path,cancellation);return reader.ReadRows(sheetName,cancellation).Select(r=>r.Cells.Select(c=>c.DisplayText).ToList()).ToList();}
    private ExcelCell ReadCell(ICell? cell,int column)
    {
        if(cell is null||cell.CellType==CellType.Blank)return new(column,ExcelCellKind.Blank,"","");
        var kind=cell.CellType switch
        {
            CellType.Numeric=>DateUtil.IsCellDateFormatted(cell)?ExcelCellKind.Date:ExcelCellKind.Number,
            CellType.Formula=>ExcelCellKind.Formula,CellType.Error=>ExcelCellKind.Error,CellType.Boolean=>ExcelCellKind.Boolean,_=>ExcelCellKind.Text
        };
        var raw=kind==ExcelCellKind.Date?cell.DateCellValue?.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)??""
            :kind==ExcelCellKind.Number?cell.NumericCellValue.ToString("R",CultureInfo.InvariantCulture):cell.ToString()??"";
        return new(column,kind,raw,formatter.FormatCellValue(cell));
    }
    public void Dispose(){if(disposed)return;disposed=true;workbook.Dispose();}
}
