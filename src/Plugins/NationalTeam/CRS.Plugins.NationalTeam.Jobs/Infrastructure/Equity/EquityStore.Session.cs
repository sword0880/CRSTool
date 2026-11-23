using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>直接持股领域独立持有实现；只通过会话端口复用连接、取消、存证和幂等机制。</summary>
internal sealed partial class EquityStore(IMarketRepositorySession session)
{
    private readonly IMarketRepositorySession session = session;
    private SqliteConnection db => session.Connection;
    private RawEvidenceStore raw => session.RawEvidence;
    private Action? beforeWrite => session.BeforeWrite;
    private TimeProvider clock => session.Clock;
    private CancellationToken operationCancellation => session.OperationCancellation;
    private string Now => session.Now;
    private SqliteCommand Command(string sql, params (string, object?)[] args) => session.Command(sql, args);
    private void Execute(string sql, params (string, object?)[] args) => session.Execute(sql, args);
    private object? Scalar(string sql, params (string, object?)[] args) => session.Scalar(sql, args);
    private bool Committed(Guid? id) => session.Committed(id);
    private void RecordCommand(Guid? id, string operation) => session.RecordCommand(id, operation);
    private void ValidateEvidence() => session.ValidateEvidence();
    private string RecordFetch(string hash, string sourceUrl, string? originalHash = null,
        string provider = "ReviewedNormalizedManualImport", string parserVersion = "normalized-csv-v1")
        => session.RecordFetch(hash, sourceUrl, originalHash, provider, parserVersion);
    private static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
