using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Internal shared SQLite session for feature stores. All feature writes use the same connection and operation token;
/// read sessions still receive their own read-only connection and snapshot through MarketRepository.Read.
/// </summary>
internal interface IMarketRepositorySession
{
    SqliteConnection Connection { get; }
    RawEvidenceStore RawEvidence { get; }
    Action? BeforeWrite { get; }
    TimeProvider Clock { get; }
    CancellationToken OperationCancellation { get; }
    SqliteCommand Command(string sql,params (string,object?)[] args);
    void Execute(string sql,params (string,object?)[] args);
    object? Scalar(string sql,params (string,object?)[] args);
    void CheckReadLimit(int count);
    void RecordCommand(Guid? id,string operation);
}
