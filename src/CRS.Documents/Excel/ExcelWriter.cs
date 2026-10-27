using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
namespace CRS.Documents.Excel;

/// <summary>工作表显示选项；业务表名、列定义和金额格式由调用方决定。</summary>
public sealed record ExcelSheetOptions(bool BoldHeader=false,string? DecimalFormat=null,IReadOnlyList<int>? ColumnWidths=null,
    bool IntegersAsNumbers=true,bool FreezeHeader=true);

/// <summary>共用工作簿生成器，只处理单元格和样式；不保存路径、认证状态、密码或业务结果。</summary>
public sealed class ExcelWriter : IDisposable
{
    private readonly XSSFWorkbook workbook=new();
    private readonly Dictionary<string,ICellStyle> numberStyles=new(StringComparer.Ordinal);
    private ICellStyle? boldHeader;
    private bool disposed;
    public void AddSheet(string name,IReadOnlyList<string> headers,IEnumerable<object?[]> rows,ExcelSheetOptions? options=null,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();options??=new();
        var sheet=workbook.CreateSheet(name);var top=sheet.CreateRow(0);
        if(options.BoldHeader&&boldHeader is null)
        {boldHeader=workbook.CreateCellStyle();var font=workbook.CreateFont();font.IsBold=true;boldHeader.SetFont(font);}
        ICellStyle? number=null;
        if(options.DecimalFormat is {} format&&!numberStyles.TryGetValue(format,out number))
        {number=workbook.CreateCellStyle();number.DataFormat=workbook.CreateDataFormat().GetFormat(format);numberStyles.Add(format,number);}
        for(var i=0;i<headers.Count;i++)
        {
            cancellation.ThrowIfCancellationRequested();var cell=top.CreateCell(i);cell.SetCellValue(headers[i]);
            if(options.BoldHeader)cell.CellStyle=boldHeader;
            sheet.SetColumnWidth(i,options.ColumnWidths is {} widths&&i<widths.Count?widths[i]:24*256);
        }
        var index=1;
        foreach(var values in rows)
        {
            cancellation.ThrowIfCancellationRequested();var row=sheet.CreateRow(index++);
            for(var i=0;i<values.Length;i++)
            {
                cancellation.ThrowIfCancellationRequested();var cell=row.CreateCell(i);
                if(values[i] is decimal amount){cell.SetCellValue((double)amount);if(number is not null)cell.CellStyle=number;}
                else if(options.IntegersAsNumbers&&values[i] is int integer)cell.SetCellValue(integer);
                // 明确写文本，不把证券名、备注、账户或等号前缀猜成公式。
                else cell.SetCellValue(values[i]?.ToString()??"");
            }
        }
        if(options.FreezeHeader)sheet.CreateFreezePane(0,1);
    }
    /// <summary>写入调用方流并保持其打开；原子文件提交、敏感导出和加密由调用方协调。</summary>
    public void Write(Stream destination,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);cancellation.ThrowIfCancellationRequested();
        using var guarded=new CancelableOutput(destination,cancellation);workbook.Write(guarded,true);cancellation.ThrowIfCancellationRequested();
    }
    public void Dispose(){if(disposed)return;disposed=true;workbook.Dispose();}
    private sealed class CancelableOutput(Stream inner,CancellationToken token):Stream
    {
        public override bool CanRead=>false;public override bool CanWrite=>inner.CanWrite;public override bool CanSeek=>inner.CanSeek;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override void Flush(){token.ThrowIfCancellationRequested();inner.Flush();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin){token.ThrowIfCancellationRequested();return inner.Seek(offset,origin);}
        public override void SetLength(long value){token.ThrowIfCancellationRequested();inner.SetLength(value);}
        public override void Write(byte[] buffer,int offset,int count){token.ThrowIfCancellationRequested();inner.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){token.ThrowIfCancellationRequested();inner.Write(buffer);}
        public override void WriteByte(byte value){token.ThrowIfCancellationRequested();inner.WriteByte(value);}
    }
}
