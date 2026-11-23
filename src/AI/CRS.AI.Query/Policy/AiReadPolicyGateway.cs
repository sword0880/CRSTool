using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CRS.AI.Contracts;

namespace CRS.AI.Query;

/// <summary>单进程准入与两阶段审计。审计成功后才访问 Worker 或返回结果。</summary>
internal sealed class AiReadPolicyGateway : IAiReadGateway
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly IAiReadGateway _inner;
    private readonly BoundedAiAuditWriter _audit;
    private readonly AiReadPolicyLimits _limits;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<long>> _recent = new(StringComparer.Ordinal);
    private readonly Queue<long> _total = new();

    public AiReadPolicyGateway(IAiReadGateway inner, IAiReadAuditSink audit, AiReadPolicyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxCallsPerMinute is < 1 or > 10000 ||
            limits.MaxConcurrent is < 1 or > 256 ||
            limits.MaxSubjects is < 1 or > 100000 ||
            limits.MaxTotalCallsPerMinute is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _inner = inner;
        _audit = new(audit);
        _limits = limits;
        _slots = new SemaphoreSlim(limits.MaxConcurrent, limits.MaxConcurrent);
    }

    public IReadOnlyList<AiTool> Tools => _inner.Tools;

    public async Task<AiResult> ExecuteAsync(AiCaller caller, AiCall call, CancellationToken token)
    {
        var safeTool = call is not null && AiCatalog.Tools.Any(t =>
            string.Equals(t.Name, call.Tool, StringComparison.Ordinal))
            ? call.Tool : "unlisted";
        // 先限制准入，再写持久审计，避免拒绝流量耗尽文件容量或占用全部写入机会。
        if (caller is null || !caller.PublicResearchAuthorized ||
            string.IsNullOrWhiteSpace(caller.Subject) || caller.Subject.Length > 256)
            return AiResult.Reject(safeTool, "Unauthorized");
        if (token.IsCancellationRequested)
            return AiResult.Reject(safeTool, "Cancelled");
        if (!_slots.Wait(0))
            return AiResult.Reject(safeTool, "ConcurrencyExceeded");

        try
        {
            var subjectHash = Fingerprint(caller.Subject);
            if (!Reserve(subjectHash)) return AiResult.Reject(safeTool, "BudgetExceeded");
            var start = new AiReadAuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow,
                subjectHash, safeTool, "Attempt", "Received", ArgumentsFingerprint(call));
            if (!await _audit.WriteAsync(start)) return AiResult.Reject(safeTool, "AuditUnavailable");

            AiResult result;
            try
            {
                token.ThrowIfCancellationRequested();
                result = call is null || safeTool == "unlisted"
                    ? AiResult.Reject(safeTool, "UnknownTool")
                    : await _inner.ExecuteAsync(caller, call, token);
            }
            catch (OperationCanceledException)
            {
                result = AiResult.Reject(safeTool, token.IsCancellationRequested ? "Cancelled" : "UpstreamTimeout");
            }
            catch (Exception)
            {
                // 上游异常只映射为固定代码，不回显原始错误信息。
                result = AiResult.Reject(safeTool, "UpstreamFailure");
            }
            if (token.IsCancellationRequested) result = AiResult.Reject(safeTool, "Cancelled");
            var finished = await FinishAsync(start, result);
            // 取消也可能发生在结果审计期间，审计失败仍保留明确的失败原因。
            return token.IsCancellationRequested && finished.Code != "AuditUnavailable"
                ? AiResult.Reject(safeTool, "Cancelled") : finished;
        }
        finally
        {
            _slots.Release();
        }
    }

    private bool Reserve(string subjectHash)
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            Prune(_total, now);
            if (_total.Count >= _limits.MaxTotalCallsPerMinute) return false;
            foreach (var entry in _recent.ToArray())
            {
                Prune(entry.Value, now);
                if (entry.Value.Count == 0) _recent.Remove(entry.Key);
            }
            if (!_recent.TryGetValue(subjectHash, out var calls))
            {
                if (_recent.Count >= _limits.MaxSubjects) return false;
                calls = new Queue<long>();
                _recent.Add(subjectHash, calls);
            }
            if (calls.Count >= _limits.MaxCallsPerMinute) return false;
            calls.Enqueue(now);
            _total.Enqueue(now);
            return true;
        }
    }

    private static void Prune(Queue<long> calls, long now)
    {
        while (calls.Count > 0 && Stopwatch.GetElapsedTime(calls.Peek(), now) >= Window)
            calls.Dequeue();
    }

    private async Task<AiResult> FinishAsync(AiReadAuditEvent start, AiResult result)
    {
        var entry = start with
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Phase = "Outcome",
            Decision = result.Status == "Rejected" ? result.Code ?? "Rejected" : result.Status
        };
        return await _audit.WriteAsync(entry) ? result : AiResult.Reject(start.Tool, "AuditUnavailable");
    }

    private static string ArgumentsFingerprint(AiCall? call)
    {
        if (call is null) return "invalid";
        try
        {
            var raw = call.Arguments.GetRawText();
            return raw.Length <= 4096 ? Fingerprint(raw) : "oversize";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return "invalid";
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
