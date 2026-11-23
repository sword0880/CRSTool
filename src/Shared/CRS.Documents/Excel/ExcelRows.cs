namespace CRS.Documents.Excel;

public enum ExcelCellKind { Blank,Text,Number,Date,Boolean,Formula,Error }

/// <summary>脱离NPOI生命周期的单元格事实；列索引从0开始。原值文本与显示格式分开，不执行公式。</summary>
public sealed record ExcelCell(int ColumnIndex,ExcelCellKind Kind,string RawText,string DisplayText);

/// <summary>保留原始工作表行号，从1开始；缺失单元格不补造成零。</summary>
public sealed record ExcelRow(int Number,IReadOnlyList<ExcelCell> Cells)
{
    public ExcelCell? GetCell(int columnIndex)=>columnIndex>=0&&columnIndex<Cells.Count?Cells[columnIndex]:null;
}
