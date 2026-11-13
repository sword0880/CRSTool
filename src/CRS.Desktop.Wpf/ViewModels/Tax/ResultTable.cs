using System.Collections;
namespace CRS.DesktopClient.ViewModels;
/// <summary>表格展示数据；保留泛型集合类型以便空结果仍生成字段列。</summary>
public sealed record ResultTable(string Title, IEnumerable Rows);
