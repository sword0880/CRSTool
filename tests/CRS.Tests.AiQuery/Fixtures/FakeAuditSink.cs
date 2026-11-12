using CRS.AI.Query;

namespace CRS.Tests.AiQuery.Fixtures;

internal sealed class FakeAuditSink : IAiReadAuditSink
{
    public int? FailAt { get; init; }
    public List<AiReadAuditEvent> Entries { get; } = [];
    public Task WriteAsync(AiReadAuditEvent entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Entries.Count + 1 == FailAt) throw new IOException("synthetic audit outage");
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}
