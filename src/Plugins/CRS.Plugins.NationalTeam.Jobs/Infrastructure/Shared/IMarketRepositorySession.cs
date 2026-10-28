using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Internal shared SQLite session for feature stores. All feature writes use the same connection and operation token;
/// read sessions still receive their own read-only connection and snapshot through MarketRepository.Read.
/// </summary>
internal interface IMarketRepositorySession
{
    SqliteConnection Connection { get; }
    bool IsReadSnapshot { get; }
    DateTimeOffset? ReadSnapshotCutoff {get;}
    RawEvidenceStore RawEvidence { get; }
    Action? BeforeWrite { get; }
    TimeProvider Clock { get; }
    CancellationToken OperationCancellation { get; }
    SqliteCommand Command(string sql,params (string,object?)[] args);
    void Execute(string sql,params (string,object?)[] args);
    object? Scalar(string sql,params (string,object?)[] args);
    void CheckReadLimit(int count);
    void RecordCommand(Guid? id,string operation);
    string Now { get; }
    bool Committed(Guid? id);
    IReadOnlyDictionary<string,bool> ValidateEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false);
    string RecordFetch(string hash,string sourceUrl,string? originalHash=null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1");
}
