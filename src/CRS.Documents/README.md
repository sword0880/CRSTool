# CRS.Documents

跨CRS主程序和业务插件复用的文件读写机制，按格式分目录。工程只依赖PDF/Excel提供方及其运行库，不引用业务、数据库、安全会话或WPF。

| 目录 | 通用职责 | 由调用方负责 |
| --- | --- | --- |
| `Pdf/` | 有界页面、文字与坐标读取；按基线拼接行及截取矩形单元格，保留原始换行，返回不依赖PdfPig的事实类型 | 券商字段、持有人模板、来源及证据核验 |
| `Excel/` | XLS/XLSX工作表读取、物理行列位置、单元格类型、原始值与显示值；工作簿生成、格式、列宽和冻结行 | 券商字段、税务/绩效计算、报告内容、敏感导出授权、原子文件提交和加密 |

```csharp
using CRS.Documents.Excel;

using var reader=ExcelReader.Open(input,cancellation);
foreach(var row in reader.EnumerateRows("明细",cancellation))
{
    // 解析与校验属于当前业务；不要使用已舍入的显示值计算金额。
    var original=row.GetCell(0)?.RawText;
}

using var writer=new ExcelWriter();
writer.AddSheet("明细",["名称","金额"],[["示例",12.345m]],
    new(BoldHeader:true,DecimalFormat:"0.00########"),cancellation);
writer.Write(output,cancellation);
```

读取上限25MiB、最大物理行索引100000、最多1000列；不执行公式、宏或外部链接。公式/错误保留明确类型，由业务决定拒绝还是显示。小数原值用往返格式，日期保留到秒，不猜时区。`ReadSheet`只供显示/检查；财务解析应读取`RawText`并单独验证类型。

读取及写入保持调用方流打开。Excel对象模型构建属于第三方同步操作，在构建前后检查取消，逐行读取、构建及流写入继续检查取消；不承诺可中断第三方内部解压或建模。写入中的外部字符串始终为文字，不自动当成公式。工作簿实例按操作创建并释放，不跨业务共享可变状态。

金额转Excel数值沿用已有double输出，仅用于报告显示；计算仍由业务层decimal负责。权限、锁定、明文/密文、内存清理和目标文件保护仍由基础设施协调，不放入文档项目。
