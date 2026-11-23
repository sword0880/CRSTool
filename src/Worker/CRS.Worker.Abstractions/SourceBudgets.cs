namespace CRS.Worker.Abstractions;

/// <summary>可选宿主能力；插件只能租用公开来源预算，不能取得宿主存储或密钥。</summary>
public interface IBackgroundSourceContext
{
    void ImportSourceState(string host, SourceBudgetState legacy);
    Task<IBackgroundSourceLease> AcquireSourceAsync(string host, SourceBudgetState legacy, CancellationToken token);
    void ReportSourceFailure(string host, int status, DateTimeOffset? retryAfter = null);
}

public interface IBackgroundSourceLease : IDisposable
{
    void Complete(int status, DateTimeOffset? retryAfter = null);
}

public sealed record SourceBudgetState(DateTimeOffset NextUtc, DateTimeOffset BlockedUtc, int Failures);
public sealed record SourceBudgetHealth(string Source, string State, int Active, int Waiting,
    DateTimeOffset NextUtc, DateTimeOffset BlockedUtc, int Failures, long Deferred, long Rejected);

public sealed class SourceBudgetUnavailableException(string code, DateTimeOffset? until = null) : InvalidOperationException(code)
{
    public string Code { get; } = code;
    public DateTimeOffset? Until { get; } = until;
}
