using System.Text.Json;
using CRS.AI.Query;

namespace CRS.AI.Infrastructure;

/// <summary>
/// 本地只追加审计写入器：文件及其访问控制必须由可信安装程序预先创建。
/// 此组件不负责权限配置、轮转、留存或防篡改，不能单独作为生产安全验收依据。
/// </summary>
public sealed class AiAppendOnlyFileAuditSink : IAiReadAuditSink
{
    private const long DefaultMaxFileBytes = 64L * 1024 * 1024;
    private readonly string _path;
    private readonly long _maxFileBytes;
    private readonly string? _maintenanceLockPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public AiAppendOnlyFileAuditSink(string absolutePath, long maxFileBytes = DefaultMaxFileBytes)
        : this(absolutePath, null, maxFileBytes) { }

    public AiAppendOnlyFileAuditSink(string absolutePath, string? maintenanceLockPath,
        long maxFileBytes = DefaultMaxFileBytes)
    {
        if (maxFileBytes is < 4096 or > AuditFileRules.MaximumArchiveBytes)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        _path = AuditFileRules.LocalPath(absolutePath);
        _maxFileBytes = maxFileBytes;
        _maintenanceLockPath = maintenanceLockPath is null ? null : AuditFileRules.LocalPath(maintenanceLockPath);
    }

    public async Task WriteAsync(AiReadAuditEvent entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditFileRules.ValidateEvent(entry);
        // 仅序列化网关产生的最小审计契约，绝不附加提示词、原始参数或查询结果。
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > AuditFileRules.MaximumEventBytes)
            throw new InvalidDataException("Audit record exceeds the allowed size.");
        // 一次提交整行，缩短取消或断电发生在事件与换行之间的窗口。
        var line = new byte[payload.Length + 1];
        payload.CopyTo(line, 0);
        line[^1] = (byte)'\n';

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var maintenance = _maintenanceLockPath is null
                ? null : AiAuditMaintenanceLease.OpenWriter(_maintenanceLockPath);
            AuditFileRules.RejectReparsePoints(_path);
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
            if (length > _maxFileBytes - line.Length)
                throw new IOException("Audit file has reached its capacity.");

            stream.Position = length;
            await stream.WriteAsync(line, cancellationToken);
            // 同步要求操作系统刷盘；依然不能替代磁盘可靠性及独立审计留存。
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

}
