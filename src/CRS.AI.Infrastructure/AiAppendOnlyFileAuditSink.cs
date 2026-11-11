using System.Text.Json;
using CRS.AI.Query;
using CRS.AI.Contracts;

namespace CRS.AI.Infrastructure;

/// <summary>
/// 本地只追加审计写入器：文件及其访问控制必须由可信安装程序预先创建。
/// 此组件不负责权限配置、轮转、留存或防篡改，不能单独作为生产安全验收依据。
/// </summary>
public sealed class AiAppendOnlyFileAuditSink : IAiReadAuditSink
{
    private const long DefaultMaxFileBytes = 64L * 1024 * 1024;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly string _path;
    private readonly long _maxFileBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly HashSet<string> AllowedDecisions = new(StringComparer.Ordinal)
    {
        "Received", "Rejected", "Partial", "NotReady", "Unauthorized", "UnknownTool",
        "InvalidArguments", "BudgetExceeded", "ConcurrencyExceeded", "Cancelled",
        "UpstreamTimeout", "UpstreamUnavailable", "UpstreamFailure", "UpstreamInvalid",
        "Unsupported", "AuditUnavailable"
    };

    public AiAppendOnlyFileAuditSink(string absolutePath, long maxFileBytes = DefaultMaxFileBytes)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath) ||
            absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Audit file must use an absolute local path.", nameof(absolutePath));
        if (maxFileBytes is < 4096 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        _path = Path.GetFullPath(absolutePath);
        _maxFileBytes = maxFileBytes;
    }

    public async Task WriteAsync(AiReadAuditEvent entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEvent(entry);
        // 仅序列化网关产生的最小审计契约，绝不附加提示词、原始参数或查询结果。
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > 1024)
            throw new InvalidDataException("Audit record exceeds the allowed size.");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoints(_path);
            // 不允许自动创建审计文件；未完成 ACL 配置或文件丢失时必须失败关闭。
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            var length = stream.Length;
            if (length > 0)
            {
                stream.Position = length - 1;
                if (stream.ReadByte() != (byte)'\n')
                    throw new InvalidDataException("Audit log has an incomplete trailing record.");
            }
            // 禁止自动删除或覆盖审计历史；容量耗尽由上层拒绝工具访问。
            if (length > _maxFileBytes - payload.Length - NewLine.Length)
                throw new IOException("Audit file has reached its capacity.");

            stream.Position = length;
            await stream.WriteAsync(payload, cancellationToken);
            await stream.WriteAsync(NewLine, cancellationToken);
            // 同步要求操作系统刷盘；依然不能替代磁盘可靠性及独立审计留存。
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void ValidateEvent(AiReadAuditEvent entry)
    {
        // 文件层再次校验白名单，防止误将主体明文或任意模型文本写入审计文件。
        if (entry.RequestId == Guid.Empty || entry.TimestampUtc.Offset != TimeSpan.Zero ||
            entry.Tool is not (AiCatalog.Overview or AiCatalog.Coverage or "unlisted") ||
            entry.Phase is not ("Attempt" or "Outcome") ||
            !AllowedDecisions.Contains(entry.Decision) ||
            !IsSha256OrMarker(entry.SubjectSha256, "unverified", null) ||
            !IsSha256OrMarker(entry.ArgumentSha256, "invalid", "oversize"))
            throw new InvalidDataException("Audit event does not match its allowed schema.");
    }

    private static bool IsSha256OrMarker(string? value, string marker1, string? marker2)
    {
        if (value == marker1 || value == marker2 && marker2 is not null)
            return true;
        return value is { Length: 64 } &&
               value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    private static void RejectReparsePoints(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Audit file cannot be a reparse point.");

        // 拒绝已存在的符号链接或目录联接；部署时仍须独立配置并检查 NTFS ACL。
        for (DirectoryInfo? directory = Directory.GetParent(path);
             directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Audit directory cannot be a reparse point.");
        }
    }
}
