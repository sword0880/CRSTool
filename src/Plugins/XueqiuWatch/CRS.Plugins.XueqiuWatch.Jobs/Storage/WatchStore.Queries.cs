using CRS.Plugins.XueqiuWatch.Core;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private static ReviewFilter ValidateFilter(ReviewFilter? input)
    {
        var filter = input ?? new();
        if (filter.Days is not (0 or 7 or 30) || filter.Offset is < 0 or > 1_000_000 ||
            filter.Mode is not ("latest" or "timeline" or "pending" or "questions" or "other"))
            throw new ArgumentException("筛选条件无效。");
        return filter with
        {
            Search = WatchInput.Text(filter.Search, 80),
            Market = WatchInput.Text(filter.Market, 10),
            Industry = WatchInput.Text(filter.Industry, 30),
            Account = string.IsNullOrWhiteSpace(filter.Account) ? "" : WatchInput.AccountId(filter.Account)
        };
    }

    private Author[] Authors()
    {
        using var command = Command("SELECT body FROM authors ORDER BY rowid", null);
        using var reader = command.ExecuteReader();
        var rows = new List<Author>();
        while (reader.Read()) rows.Add(WatchProtocol.Decode<Author>(reader.GetString(0)));
        return rows.ToArray();
    }

    private Security[] Catalog(string? search)
    {
        using var command = Command("SELECT body FROM securities ORDER BY rowid DESC", null);
        using var reader = command.ExecuteReader();
        var value = WatchInput.Text(search, 80);
        var rows = new List<Security>();
        while (rows.Count < 80 && reader.Read())
        {
            var security = WatchProtocol.Decode<Security>(reader.GetString(0));
            if (Matches(security, value)) rows.Add(security);
        }
        return rows.ToArray();
    }

    private static bool Matches(Security security, string search) => search.Length == 0 ||
        security.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        security.Code.Contains(search, StringComparison.OrdinalIgnoreCase);

    private sealed record Stored(Opinion Opinion, string Source);

    private Stored[] Load(ReviewFilter filter, CancellationToken token)
    {
        var now = WatchProtocol.BeijingNow;
        var since = filter.Days == 0 ? 0 : new DateTimeOffset(
            now.Date.AddDays(1 - filter.Days), now.Offset).ToUnixTimeSeconds();
        using var command = Command("""
            SELECT o.body,o.source,s.body FROM opinions o LEFT JOIN securities s
                ON s.market=o.market AND s.code=o.code
            WHERE o.posted >= $since AND ($account='' OR o.account=$account)
                AND ($market='' OR o.market=$market) AND ($code='' OR o.code=$code)
            ORDER BY o.posted DESC,o.rowid DESC
            """, null, ("$since", since), ("$account", filter.Account ?? ""),
            ("$market", filter.Security?.Market ?? filter.Market ?? ""), ("$code", filter.Security?.Code ?? ""));
        using var reader = command.ExecuteReader();
        var rows = new List<Stored>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var opinion = WatchProtocol.Decode<Opinion>(reader.GetString(0));
            if (!reader.IsDBNull(2)) opinion = opinion with { Security = WatchProtocol.Decode<Security>(reader.GetString(2)) };
            rows.Add(new(opinion, reader.GetString(1)));
        }
        return rows.ToArray();
    }

    private static IEnumerable<Stored> CurrentStances(IEnumerable<Stored> rows) => rows
        .Where(x => x.Opinion.Attribution == Attribution.Author && x.Opinion.Stance is Stance.Bullish or Stance.Bearish)
        .DistinctBy(x => x.Opinion.Account);

    private WatchOverview Overview(ReviewFilter filter, CancellationToken token)
    {
        var rows = Load(filter, token);
        var stocks = rows.Where(x => x.Opinion.Security is not null).GroupBy(x => x.Opinion.Security!.Key)
            .Select(group =>
            {
                var current = CurrentStances(group).ToArray();
                return new StockSummary(group.First().Opinion.Security!,
                    current.Count(x => x.Opinion.Stance == Stance.Bullish),
                    current.Count(x => x.Opinion.Stance == Stance.Bearish),
                    group.Where(x => x.Opinion.Attribution == Attribution.Author)
                        .DistinctBy(x => (x.Opinion.Account, x.Source)).Count(),
                    group.Count(x => x.Opinion.Stance == Stance.Pending), group.First().Opinion.PublishedAt);
            }).ToArray();
        var industries = stocks.Select(x => x.Security.Industry).Where(x => x.Length > 0).Distinct().Order().Take(80).ToArray();
        var filtered = stocks.Where(x => (filter.Industry is "" or null || x.Security.Industry ==
                (filter.Industry == "未分类" ? "" : filter.Industry))
            && Matches(x.Security, filter.Search ?? ""))
            .OrderBy(x => x.Security.Industry.Length == 0)
            .ThenBy(x => x.Security.Industry).ThenByDescending(x => x.Discussions).ThenByDescending(x => x.LatestAt)
            .ThenBy(x => x.Security.Key).ToArray();
        return new(filtered.Skip(filter.Offset).Take(WatchProtocol.StocksPerPage).ToArray(), industries, filtered.Length,
            rows.Count(x => x.Opinion.Security is null),
            rows.Length == 0 ? null : rows.Min(x => x.Opinion.PublishedAt),
            rows.Length == 0 ? null : rows.Max(x => x.Opinion.PublishedAt),
            rows.Length == 0 ? null : rows.Max(x => x.Opinion.RecordedAt));
    }

    private OpinionDetail Details(ReviewFilter filter, CancellationToken token)
    {
        var rows = Load(filter, token);
        IEnumerable<Stored> selected = filter.Mode switch
        {
            "latest" when filter.Security is not null => CurrentStances(rows).OrderBy(x => x.Opinion.Stance),
            "timeline" when filter.Security is not null => rows,
            "other" when filter.Security is not null => rows.Where(x => x.Opinion.Stance is Stance.Pending or Stance.Neutral ||
                x.Opinion.Attribution != Attribution.Author),
            "pending" => rows.Where(x => x.Opinion.Security is null),
            "questions" => rows.Where(x => x.Opinion.Question.Length != 0),
            _ => []
        };
        var opinions = selected.Select(x => x.Opinion).ToArray();
        return new(opinions.Skip(filter.Offset).Take(WatchProtocol.OpinionsPerPage).ToArray(), opinions.Length);
    }
}
