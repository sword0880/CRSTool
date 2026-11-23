using CRS.Domain.Performance;

namespace CRS.DesktopClient.ViewModels;

public sealed record PerformanceAccountOverviewTotal(
    DateOnly Date, string Currency, decimal? NetAssets, int NetAssetAccounts, decimal? Cash, int CashAccounts)
{
    public string NetAssetsDisplay => Format(NetAssets, NetAssetAccounts);
    public string CashDisplay => Format(Cash, CashAccounts);

    internal static PerformanceAccountOverviewTotal[] FromAccounts(IEnumerable<PerformanceAccountOverviewRow> accounts)
        => accounts.SelectMany(Amounts).GroupBy(a => (a.Date, a.Currency))
            .OrderByDescending(g => g.Key.Date).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g =>
            {
                var netAssets = g.Where(a => a.IsNetAssets).Select(a => a.Amount).ToArray();
                var cash = g.Where(a => !a.IsNetAssets).Select(a => a.Amount).ToArray();
                return new PerformanceAccountOverviewTotal(g.Key.Date, g.Key.Currency,
                    Sum(netAssets), netAssets.Length, Sum(cash), cash.Length);
            }).ToArray();

    private static IEnumerable<(DateOnly Date, string Currency, bool IsNetAssets, decimal Amount)> Amounts(PerformanceAccountOverviewRow account)
    {
        if (account.Date is not { } date) yield break;
        foreach (var amount in DisplayedAmounts(account.Currency, account.NetAssets, account.NetAssetsByCurrency))
            yield return (date, amount.Currency, true, amount.Amount!.Value);
        foreach (var amount in DisplayedAmounts(account.Currency, account.Cash, account.CashByCurrency))
            yield return (date, amount.Currency, false, amount.Amount!.Value);
    }

    private static IEnumerable<PerformanceCurrencyAmount> DisplayedAmounts(
        string? currency, decimal? converted, PerformanceCurrencyAmount[] native)
        => currency is not null && converted.HasValue ? [new(currency, converted)] : native.Where(a => a.Amount.HasValue);

    private static decimal? Sum(decimal[] amounts)
    {
        if (amounts.Length == 0) return null;
        try { return amounts.Sum(); }
        catch (OverflowException) { return null; }
    }

    private static string Format(decimal? value, int accounts)
        => value is { } amount ? $"{amount:N2}（{accounts} 个账户）" : accounts == 0 ? "—" : "金额超出范围";
}
