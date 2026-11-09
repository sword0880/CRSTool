namespace CRS.DesktopClient.ViewModels;

public sealed record PerformanceAnnualAssetRow(int Year, DateOnly? Date, decimal? CoveredCny, string BrokerAmounts,
    int CoveredAccounts, int ExpectedAccounts, string Coverage, bool MissingRates)
{
    public string CoveredAmount => CoveredCny?.ToString("N2") ?? "—";
}
