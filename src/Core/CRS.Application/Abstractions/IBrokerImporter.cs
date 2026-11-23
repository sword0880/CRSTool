namespace CRS.Application.Abstractions;

/// <summary>规范化券商输入的端口；应用层不依赖具体 XML 或 Excel 实现。</summary>
public interface IBrokerImporter
{
    /// <summary>导入活动报告及可选期初资料，按安全检查点响应取消；来源 SHA256 必须取自实际解析的同一份冻结字节。</summary>
    ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default);
}
