using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>为每次导入创建独立解析器，防止同一应用服务复用旧去重状态。</summary>
public sealed class IbkrImporter : IBrokerImporter
{
    /// <summary>读取 IBKR XML 并返回规范化记录，保留既有解析和取消语义。</summary>
    public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellationToken = default)
        => new IbkrXmlParser().Parse(files, opening, cancellationToken);
}
