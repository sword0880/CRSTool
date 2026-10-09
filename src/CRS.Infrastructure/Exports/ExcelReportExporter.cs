using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>通过报告端口适配 NPOI 输出，防止界面依赖工作簿实现。</summary>
public sealed class ExcelReportExporter : IReportExporter
{
    /// <summary>导出已冻结结果和人工依据，不在导出过程中重新计算。</summary>
    public void Export(string path, CalculationResult result, IEnumerable<ReviewNote>? reviews = null)
        => ExcelReports.Export(path, result, reviews);
}
