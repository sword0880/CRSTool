namespace CRS.Application.Abstractions;

/// <summary>报告导出端口，界面和应用层不直接访问 NPOI。</summary>
public interface IReportExporter
{
    /// <summary>将冻结结果及复核记录输出至指定文件。</summary>
    void Export(string path, CalculationResult result, IEnumerable<ReviewNote>? reviews = null);
}
