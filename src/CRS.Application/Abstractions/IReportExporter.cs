namespace CRS.Application.Abstractions;

/// <summary>报告导出端口，界面和应用层不直接访问 NPOI。</summary>
public interface IReportExporter
{
    /// <summary>将冻结结果及复核记录输出至指定文件。</summary>
    void Export(string path, CalculationResult result, IEnumerable<ReviewNote>? reviews = null, CancellationToken cancellation = default);
    /// <summary>独立密码加密导出；未提供实现的测试替身明确拒绝。</summary>
    void ExportEncrypted(string path,CalculationResult result,string password,IEnumerable<ReviewNote>? reviews=null,CancellationToken cancellation=default)=>throw new NotSupportedException();
    /// <summary>解密至父目录下全新的目录，不覆盖已有资料。</summary>
    string RestoreEncrypted(string path,string parentDirectory,string password,CancellationToken cancellation=default)=>throw new NotSupportedException();
}
