namespace CRS.AI.Query;

/// <summary>最小审计事件。主体和参数使用指纹，结果与异常内容留在调用链中。</summary>
public sealed record AiReadAuditEvent(Guid RequestId, DateTimeOffset TimestampUtc,
    string SubjectSha256, string Tool, string Phase, string Decision, string ArgumentSha256);

public interface IAiReadAuditSink
{
    Task WriteAsync(AiReadAuditEvent entry, CancellationToken cancellationToken);
}
