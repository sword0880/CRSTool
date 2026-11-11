using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CRS.AI.Contracts;

namespace CRS.AI.Query;

/// <summary>
/// Audit records intentionally contain no clear-text identity, prompts, arguments, results,
/// private data, exception messages or filesystem paths. A production sink must be durable
/// and have an independently verified OS access policy.
/// </summary>
public sealed record AiReadAuditEvent(Guid RequestId, DateTimeOffset TimestampUtc,
    string SubjectSha256, string Tool, string Phase, string Decision, string ArgumentSha256);

public interface IAiReadAuditSink
{
    Task WriteAsync(AiReadAuditEvent entry, CancellationToken cancellationToken);
}

public sealed record AiReadPolicyLimits(
    int MaxCallsPerMinute = 20, int MaxConcurrent = 2, int MaxSubjects = 128);

/// <summary>
/// Mandatory composition root for both MCP and OpenAI tool adapters. No host may construct
/// a raw AI query service as its transport entry point.
/// </summary>
public static class AiReadGatewayFactory
{
    public static IAiReadGateway Create(IWorkerPublicReadClient worker, IAiReadAuditSink audit,
        AiReadPolicyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(audit);
        return new AiReadPolicyGateway(new AiReadQueryService(worker), audit, limits ?? new());
    }
}

/// <summary>
/// Process-local P1 admission guard. It deliberately cannot be treated as cross-process
/// rate limiting, persistent quota enforcement, remote identity verification or billing.
/// Audit write-before-read and write-before-release are both fail-closed.
/// </summary>
internal sealed class AiReadPolicyGateway : IAiReadGateway
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly IAiReadGateway _inner;
    private readonly IAiReadAuditSink _audit;
    private readonly AiReadPolicyLimits _limits;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<long>> _recent = new(StringComparer.Ordinal);

    public AiReadPolicyGateway(IAiReadGateway inner, IAiReadAuditSink audit, AiReadPolicyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxCallsPerMinute is < 1 or > 10000 ||
            limits.MaxConcurrent is < 1 or > 256 ||
            limits.MaxSubjects is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _inner = inner;
        _audit = audit;
        _limits = limits;
        _slots = new SemaphoreSlim(limits.MaxConcurrent, limits.MaxConcurrent);
    }

    public IReadOnlyList<AiTool> Tools => _inner.Tools;

    public async Task<AiResult> ExecuteAsync(AiCaller caller, AiCall call, CancellationToken token)
    {
        // Caller, including its trusted identity, must NEVER be constructed from model JSON.
        var safeTool = call is not null && AiCatalog.Tools.Any(t =>
            string.Equals(t.Name, call.Tool, StringComparison.Ordinal))
            ? call.Tool : "unlisted";
        var validSubject = caller is not null &&
            !string.IsNullOrWhiteSpace(caller.Subject) && caller.Subject.Length <= 256;
        var subjectHash = validSubject ? Fingerprint(caller.Subject) : "unverified";
        var argumentHash = ArgumentsFingerprint(call);
        var requestId = Guid.NewGuid();
        var start = new AiReadAuditEvent(requestId, DateTimeOffset.UtcNow,
            subjectHash, safeTool, "Attempt", "Received", argumentHash);
        if (!await WriteAuditAsync(start)) return AiResult.Reject(safeTool, "AuditUnavailable");

        if (!validSubject || !caller.PublicResearchAuthorized)
            return await FinishAsync(start, AiResult.Reject(safeTool, "Unauthorized"));
        // Unknown tools and invalid arguments also consume caller budget: model-driven
        // retries must not turn the audit path into an unlimited request channel.
        if (!Reserve(subjectHash))
            return await FinishAsync(start, AiResult.Reject(safeTool, "BudgetExceeded"));
        if (call is null || safeTool == "unlisted")
            return await FinishAsync(start, AiResult.Reject(safeTool, "UnknownTool"));
        if (token.IsCancellationRequested)
            return await FinishAsync(start, AiResult.Reject(safeTool, "Cancelled"));
        if (!_slots.Wait(0))
            return await FinishAsync(start, AiResult.Reject(safeTool, "ConcurrencyExceeded"));

        try
        {
            AiResult result;
            try
            {
                result = await _inner.ExecuteAsync(caller, call, token);
            }
            catch (OperationCanceledException)
            {
                result = AiResult.Reject(safeTool, token.IsCancellationRequested ? "Cancelled" : "UpstreamTimeout");
            }
            catch (Exception)
            {
                // Never return exception messages, stack traces, local paths or model arguments.
                result = AiResult.Reject(safeTool, "UpstreamFailure");
            }
            return await FinishAsync(start, result);
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
            foreach (var entry in _recent.ToArray())
            {
                while (entry.Value.Count > 0 &&
                       Stopwatch.GetElapsedTime(entry.Value.Peek(), now) >= Window)
                    entry.Value.Dequeue();
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
            return true;
        }
    }

    private async Task<AiResult> FinishAsync(AiReadAuditEvent start, AiResult result)
    {
        var entry = start with
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Phase = "Outcome",
            Decision = result.Status == "Rejected" ? result.Code ?? "Rejected" : result.Status
        };
        return await WriteAuditAsync(entry) ? result : AiResult.Reject(start.Tool, "AuditUnavailable");
    }

    private async Task<bool> WriteAuditAsync(AiReadAuditEvent entry)
    {
        try
        {
            // Independent short deadline: a canceled request still needs an outcome record.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _audit.WriteAsync(entry, deadline.Token).WaitAsync(deadline.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
