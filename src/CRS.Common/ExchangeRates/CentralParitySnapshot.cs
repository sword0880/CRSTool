namespace CRS.ExchangeRates;

/// <summary>公开报价缓存及本机只读查询使用同一数据形状和校验规则。</summary>
public sealed record CentralParitySnapshot(int Version, DateOnly From, DateOnly Through, DateTimeOffset FetchedAt,
    CentralParityQuote[] Quotes)
{
    public static (DateOnly From, DateOnly Through) YearEndRange(int year)
    {
        var through = new DateOnly(year, 12, 31);
        return (through.AddDays(-13), through);
    }

    public bool IsYearEnd(int year) => (From, Through) == YearEndRange(year) && IsValid(year);

    public bool IsValid(int year)
    {
        if (Version != 1 || From.Year != year || Through.Year != year || From > Through
            || Quotes is not { Length: > 0 and <= 366 }
            || !Quotes.All(q => q is { Usd: > 0 and <= 1000, Hkd: > 0 and <= 1000 })) return false;
        if (Quotes[0].Date < From || Quotes[0].Date.DayNumber - From.DayNumber > 14
            || Quotes[^1].Date > Through || Through.DayNumber - Quotes[^1].Date.DayNumber > 14) return false;
        return Quotes.Zip(Quotes.Skip(1)).All(p => p.Second.Date.DayNumber - p.First.Date.DayNumber is > 0 and <= 14);
    }
}
