namespace CRS.Application;

public enum ImportDecision { Cancel, OpenExisting, SaveNew }
public enum ImportMatchKind { Identical, SharedFiles, OverlappingPeriod }

public sealed record ImportReview(ImportMatchKind Kind, HistoryItem Existing,
    IReadOnlyList<string> Files, IReadOnlyList<string> Differences);
