namespace CRS.Domain.Performance;

public sealed record PerformanceCurrencyAmount(string Currency, decimal? Amount);

public sealed record PerformanceAccountValuation(DateOnly? Date, string? Currency, decimal? NetAssets, decimal? Cash)
{
    public PerformanceCurrencyAmount[] CashByCurrency { get; init; } = [];
    public PerformanceCurrencyAmount[] NetAssetsByCurrency { get; init; } = [];
    public string? NetAssetsUnavailableReason { get; init; }
}

public sealed record PerformanceAnnualBrokerAssets(string Broker, decimal? NetAssetsCny,
    PerformanceCurrencyAmount[] NativeAmounts, int CoveredAccounts, int ExpectedAccounts, bool MissingRates);

public sealed record PerformanceAnnualAssets(int Year, DateOnly? Date, bool MissingYearEnd,
    PerformanceAnnualBrokerAssets[] Brokers, decimal? TotalCny);

/// <summary>从本地绩效资料读取每个账户最新的日末估值。</summary>
public static class PerformanceAccountValuations
{
    public static DateOnly[] AnnualDates(PerformanceData data, DateOnly through)
    {
        var accountIds = data.Accounts.Select(a => a.Id).ToHashSet();
        return data.Valuations.Where(v => v.Moment == ValuationMoment.EndOfDay &&
                v.Date.Year >= 2000 && v.Date <= through && accountIds.Contains(v.AccountId))
            .GroupBy(v => v.Date.Year).Select(g => g.Max(v => v.Date)).Order().ToArray();
    }

    public static PerformanceAnnualAssets[] Annual(PerformanceData data, DateOnly through,
        PerformanceFx[] cachedRates, CancellationToken cancellation = default)
    {
        var dates = AnnualDates(data, through);
        if (dates.Length == 0) return [];

        var importedPairs = data.Fx.ToLookup(f => (f.From, f.To, f.Date));
        var additionalRates = cachedRates.Where(f =>
            !importedPairs.Contains((f.From, f.To, f.Date)) && !importedPairs.Contains((f.To, f.From, f.Date)));
        var context = new PerformanceCalculationContext(data with { Fx = [.. data.Fx, .. additionalRates] },
            new(data.Accounts.Select(a => a.Id).ToArray(), through, through), cancellation);
        var byYear = dates.ToDictionary(date => date.Year);
        return Enumerable.Range(dates[0].Year, dates[^1].Year - dates[0].Year + 1)
            .Select(year => byYear.TryGetValue(year, out var date)
                ? ReadYear(context, date, year < through.Year && date != new DateOnly(year, 12, 31))
                : new PerformanceAnnualAssets(year, null, false, [], null)).ToArray();
    }

    private static PerformanceAnnualAssets ReadYear(PerformanceCalculationContext context, DateOnly date, bool missingYearEnd)
    {
        // 账户一旦进入资料覆盖范围，后续年份缺少同日快照时保留缺口。
        var brokers = context.Accounts.Values
            .Where(a => a.TrustedFrom <= date || context.Valuations.Contains((a.Id, date, ValuationMoment.EndOfDay)))
            .GroupBy(a => a.Broker)
            .Select(group =>
            {
                var accounts = group.Select(a => ReadAnnualAccount(context, a.Id, date)).ToArray();
                var covered = accounts.Where(a => a.Amount.HasValue).ToArray();
                return new PerformanceAnnualBrokerAssets(group.Key,
                    covered.Length == 0 ? null : covered.Sum(a => a.Amount!.Value),
                    SumByCurrency(covered.SelectMany(a => a.Native).Select(a => (a.Currency, a.Amount))),
                    covered.Length, accounts.Length, accounts.Any(a => a.Amount is null && a.Native.Length > 0));
            }).ToArray();
        var total = !missingYearEnd && brokers.Length > 0 && brokers.All(b => b.CoveredAccounts == b.ExpectedAccounts)
            ? brokers.Sum(b => b.NetAssetsCny) : (decimal?)null;
        return new(date.Year, date, missingYearEnd, brokers, total);
    }

    private static (decimal? Amount, PerformanceCurrencyAmount[] Native) ReadAnnualAccount(
        PerformanceCalculationContext context, string id, DateOnly date)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        var diagnostics = new PerformanceDiagnostics(context);
        var valuation = new PerformanceValuationEngine(context, diagnostics);
        var snapshot = valuation.Snapshot(id, date, ValuationMoment.EndOfDay);
        if (snapshot is null) return (null, []);

        // 沿用日末估值的持仓定价和原币投影。原币净额为零时无需汇率。
        _ = ReadAmount(() => valuation.AccountNav(id, date, ValuationMoment.EndOfDay));
        var native = NativeNetAssets(snapshot, diagnostics.Exposure);
        return native.Length == 0 ? (null, []) :
            (ReadAmount(() => valuation.SumConverted(native.Where(a => a.Amount != 0)
                .Select(a => (a.Amount!.Value, a.Currency, date)))), native);
    }

    public static IReadOnlyDictionary<string, PerformanceAccountValuation> Latest(
        PerformanceData data, DateOnly through, CancellationToken cancellation = default)
    {
        var context = new PerformanceCalculationContext(data,
            new(data.Accounts.Select(a => a.Id).ToArray(), through, through), cancellation);
        var results = new Dictionary<string, PerformanceAccountValuation>();
        foreach (var account in context.Accounts.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var day = context.AccountValuations[account.Id]
                .Where(v => v.Moment == ValuationMoment.EndOfDay && v.Date <= through)
                .Max(v => (DateOnly?)v.Date);
            results.Add(account.Id, day is { } date ? Read(context, account, date) : new(null, null, null, null));
        }
        return results;
    }

    private static PerformanceAccountValuation Read(PerformanceCalculationContext context, InvestmentAccount account, DateOnly date)
    {
        var diagnostics = new PerformanceDiagnostics(context);
        var snapshot = new PerformanceValuationEngine(context, diagnostics).Snapshot(account.Id, date, ValuationMoment.EndOfDay);
        if (snapshot is null) return new(date, null, null, null) { NetAssetsUnavailableReason = "同日估值资料冲突" };

        // 基础币种未确认时，只采用快照明确列出的单一原币。
        var currencies = snapshot.Components.Select(c => c.Currency).Distinct().Take(2).ToArray();
        var currency = account.BaseCurrencyConfirmed ? account.BaseCurrency : currencies.Length == 1 ? currencies[0] : null;
        var valuation = new PerformanceValuationEngine(context, diagnostics, currency);

        var cash = snapshot.Components.Where(c => c.Kind == "Cash").ToArray();
        var cashAmount = currency is not null && cash.Length > 0 && cash.All(c => c.Amount.HasValue)
            ? ReadAmount(() => valuation.SumConverted(cash.Select(c => (c.Amount!.Value, c.Currency, date))))
            : null;
        var converted = ReadAmount(() => valuation.AccountNav(account.Id, date, ValuationMoment.EndOfDay));
        var netAssets = currency is null ? null : converted;
        return new(date, currency, netAssets, cashAmount)
        {
            CashByCurrency = cashAmount.HasValue ? [] : SumByCurrency(cash.Select(c => (c.Currency, c.Amount))),
            NetAssetsByCurrency = netAssets.HasValue ? [] : NativeNetAssets(snapshot, diagnostics.Exposure),
            NetAssetsUnavailableReason = MissingNetAssets(snapshot, diagnostics.Exposure)
        };
    }

    private static PerformanceCurrencyAmount[] NativeNetAssets(ValuationSnapshot snapshot, List<PerformanceExposure> exposure)
    {
        if (exposure.Count != snapshot.Components.Length || exposure.Any(e => e.NativeAmount is null)) return [];
        var amounts = SumByCurrency(exposure.Select(e => (e.Currency,
            e.Kind is "Liability" or "Payable" ? -e.NativeAmount : e.NativeAmount)));
        return amounts.All(a => a.Amount.HasValue) ? amounts : [];
    }

    private static string MissingNetAssets(ValuationSnapshot snapshot, List<PerformanceExposure> exposure)
    {
        if (snapshot.Quality == PerformanceQuality.InsufficientData) return "资产资料不完整";
        if (snapshot.Components.Any(c => c.Kind == "Cash" && c.Amount is null)) return "缺现金余额";
        if (exposure.Any(e => e.Kind == "Security" && e.NativeAmount is null)) return "缺持仓市值";
        return "估值资料不足";
    }

    private static PerformanceCurrencyAmount[] SumByCurrency(IEnumerable<(string Currency, decimal? Amount)> amounts)
        => amounts.GroupBy(a => a.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PerformanceCurrencyAmount(g.Key,
                g.All(a => a.Amount.HasValue) ? ReadAmount(() => g.Sum(a => a.Amount!.Value)) : null)).ToArray();

    private static decimal? ReadAmount(Func<decimal?> read)
    {
        try { return read(); }
        catch (OverflowException) { return null; }
        catch (CrsException) { return null; }
    }
}
