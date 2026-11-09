namespace CRS.Application;

public enum HistoricalOriginalKind { Activity, Income, Opening }

public sealed record HistoricalOriginal(string Name, string Sha256, HistoricalOriginalKind Kind, bool InVault = false);

public sealed record HistoricalRecalculationPlan(string SourceId, string Broker, int Year, bool OpeningZero,
    bool ScopeConfirmed, bool NeedsCarry, bool HasAccountPeriods, IReadOnlyList<HistoricalOriginal> Originals)
{
    public bool HasVaultCarry { get; init; }
}

public sealed record HistoricalRecalculationRequest(string SourceId, IReadOnlyDictionary<string,string> LocalFiles,
    string? CarryPath, bool SaveCanonicalSnapshot);

public sealed record HistoricalOriginalInput(string Name, string Sha256, string? LocalPath);

public sealed record ImportHistoryComparison(string Id, string CreatedUtc, bool Complete, int Issues, bool IsArchived,
    decimal? RealizedGainCny, decimal? EstimatedTopUpCny, IReadOnlyList<string> Differences, bool IsSelected);
