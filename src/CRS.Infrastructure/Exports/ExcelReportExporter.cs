using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>通过报告端口适配 NPOI 输出，防止界面依赖工作簿实现。</summary>
public sealed class ExcelReportExporter(Action<Action>? commitGuard=null) : IReportExporter
{
    /// <summary>导出已冻结结果和人工依据，不在导出过程中重新计算。</summary>
    public void Export(string path, CalculationResult result, IEnumerable<ReviewNote>? reviews = null,CancellationToken cancellation = default)
        => ExcelReports.Export(path, result, reviews,cancellation,commitGuard);
    // 两种提交都绑定原解锁会话，锁定后旧操作不能提交。
    public void ExportEncrypted(string path,CalculationResult result,string password,IEnumerable<ReviewNote>? reviews=null,CancellationToken cancellation=default)
        =>EncryptedReportFiles.Export(path,result,password,reviews,cancellation,commitGuard);
    public string RestoreEncrypted(string path,string parentDirectory,string password,CancellationToken cancellation=default)
        =>EncryptedReportFiles.Restore(path,parentDirectory,password,cancellation,commitGuard);
}
