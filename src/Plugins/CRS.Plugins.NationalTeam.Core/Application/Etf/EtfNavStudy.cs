using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record EtfNavStudyRequest(EtfKey Key, DateOnly From, DateOnly Through, DateTimeOffset KnowledgeCutoff,
    NavResearchDataset Nav, CoverageReport Coverage, IReadOnlyList<SharePoint> Shares, IReadOnlyList<EvidenceRow> ShareEvidence,
    AlertFundMetadata? Comparability);
public sealed record EtfNavStudyWindow(DateOnly Date, int Window, decimal? DeltaShares, decimal? EstimatedAmountYuan, string State, string Fingerprint);
public sealed record EtfNavStudyReport(string AlgorithmVersion, string Fingerprint, EtfNavStudyRequest FrozenInputs, IReadOnlyList<EtfNavStudyWindow> Windows)
{
    public string State => Windows.Count == 0 ? "NoRegisteredTradingDays" : Windows.All(x => x.EstimatedAmountYuan.HasValue) ? "CompleteStudyWindows" : "IncompleteStudyWindows";
    public string KnowledgeMode => "RetrospectiveOnly";
    public string TimingProof => "NotProven";
    public string InstitutionTradeState => "NotObserved";
    public string Basis => "全ETF份额变化按同日单位净值估算；不是国家队成交量、毛交易额或精确现金净流入。";
}

/// <summary>冻结研究样本按已核对交易日对齐；缺日不跨越，份额可比性未知时不计算差额。</summary>
public static class EtfNavStudy
{
    public const string AlgorithmVersion = "etf-nav-study-v1";
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static EtfNavStudyReport Run(EtfNavStudyRequest input)
    {
        var cutoffDay = DateOnly.FromDateTime(input.KnowledgeCutoff.ToOffset(TimeSpan.FromHours(8)).DateTime);
        if (input.From > input.Through || input.Through > cutoffDay || input.Through.DayNumber - input.From.DayNumber > 3660
            || input.Key != new EtfKey(input.Nav.Market, input.Nav.Ticker) || input.Key != new EtfKey(input.Coverage.Market, input.Coverage.Ticker)
            || input.Coverage.From > input.From || input.Coverage.To < input.Through || input.Nav.VerifiedAt > input.KnowledgeCutoff
            || input.Nav.VerifiedAt == default || !Hash(input.Nav.Fingerprint))
            throw new InvalidDataException("研究证券、日期、NAV身份或知识截止时间无效。");
        if (input.Shares.GroupBy(x => x.Date).Any(x => x.Count() != 1) || input.ShareEvidence.GroupBy(x => x.RevisionId).Any(x => x.Count() != 1)
            || input.Coverage.Days.GroupBy(x => x.Date).Any(x => x.Count() != 1) || input.Nav.Points.GroupBy(x => x.Date).Any(x => x.Count() != 1)
            || input.Shares.Any(x => x.Shares < 0 || x.Nav is not null || string.IsNullOrWhiteSpace(x.RevisionId))
            || input.Coverage.Days.Any(x => x.Date < input.Coverage.From || x.Date > input.Coverage.To)
            || input.Coverage.RegisteredTradingDays != input.Coverage.Days.Count)
            throw new InvalidDataException("研究输入存在重复、无效份额、夹带NAV或日历边界不符。");
        var sourceHashes = input.Nav.Sources.Select(x => x.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (input.Nav.Sources.Count == 0 || input.Nav.Sources.Any(x => !Hash(x.Sha256) || x.RetrievedAt == default || x.RetrievedAt > input.Nav.VerifiedAt)
            || input.Nav.Points.Any(x => x.UnitNavYuan <= 0 || x.Date > cutoffDay || x.FirstSeenAt == default || x.FirstSeenAt > input.Nav.VerifiedAt
            || x.SourceHashes.Count == 0 || x.SourceHashes.Any(h => !Hash(h) || !sourceHashes.Contains(h))))
            throw new InvalidDataException("NAV数值、来源指纹或首见时刻无效。");
        // 所有列表复制并排序，避免调用方后续变更污染已导出的研究身份。
        var frozen = input with
        {
            Shares = Array.AsReadOnly(input.Shares.OrderBy(x => x.Date).ToArray()),
            ShareEvidence = Array.AsReadOnly(input.ShareEvidence.OrderBy(x => x.RevisionId, StringComparer.Ordinal).ToArray()),
            Coverage = input.Coverage with { Days = Array.AsReadOnly(input.Coverage.Days.OrderBy(x => x.Date).ToArray()) },
            Nav = input.Nav with
            {
                Sources = Array.AsReadOnly(input.Nav.Sources.OrderBy(x => x.Url, StringComparer.Ordinal).ToArray()),
                Points = Array.AsReadOnly(input.Nav.Points.OrderBy(x => x.Date).Select(x => x with { SourceHashes = Array.AsReadOnly(x.SourceHashes.Order(StringComparer.Ordinal).ToArray()) }).ToArray())
            }
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AlgorithmVersion + "|" + JsonSerializer.Serialize(frozen))));
        var days = frozen.Coverage.Days.Select(x => x.Date).ToArray();
        var states = frozen.Coverage.Days.ToDictionary(x => x.Date);
        var shares = frozen.Shares.ToDictionary(x => x.Date);
        var evidence = frozen.ShareEvidence.ToDictionary(x => x.RevisionId, StringComparer.Ordinal);
        var nav = frozen.Nav.Points.ToDictionary(x => x.Date);
        bool Trusted(SharePoint point)
        {
            if (!evidence.TryGetValue(point.RevisionId, out var item) || item.State != "Accepted" || item.Market != frozen.Key.Market || item.Ticker != frozen.Key.Ticker
                || item.TradeDate != point.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) || !Hash(item.Hash) || !Hash(item.OriginalHash)
                || !decimal.TryParse(item.Shares, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var quantity) || quantity != point.Shares) return false;
            // 旧版未知的取得/审核时间不能用当前研究时间猜补。
            var times = new List<DateTimeOffset>();
            foreach (var text in new[] { item.RetrievedAt, item.FirstSeenAt, item.AcceptedAt })
            {
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time == default || time > frozen.KnowledgeCutoff) return false;
                times.Add(time);
            }
            return times[0] <= times[1] && times[1] <= times[2];
        }
        var windows = new List<EtfNavStudyWindow>();
        foreach (var date in days.Where(x => x >= frozen.From && x <= frozen.Through))
        foreach (var window in new[] { 1, 5, 20 })
        {
            var index = Array.IndexOf(days, date); string? failure = null;
            if (!frozen.Coverage.CalendarVerified || !frozen.Coverage.CalendarRangeCovered) failure = "UnverifiedCalendar";
            else if (index < window) failure = "IncompleteWarmup";
            var selected = index >= window ? days[(index - window)..(index + 1)] : [];
            if (failure is null && selected.Any(d => !shares.TryGetValue(d, out var p) || states[d].State != "Accepted" || states[d].RevisionId != p.RevisionId || !Trusted(p)))
                failure = "ShareEvidenceUnavailable";
            if (failure is null && (frozen.Comparability is not { } metadata || metadata.Market != frozen.Key.Market || metadata.Ticker != frozen.Key.Ticker || !metadata.Covers(selected[0], date)))
                failure = "UnverifiedComparability";
            if (failure is not null)
            {
                windows.Add(new(date, window, null, null, failure, fingerprint)); continue;
            }
            var aligned = selected.Select(d => shares[d] with { Nav = nav.TryGetValue(d, out var value) ? value.UnitNavYuan : null }).ToArray();
            var metric = EtfMetrics.Calculate(date, window, days, aligned);
            windows.Add(new(date, window, metric.DeltaShares, metric.EstimatedFlow, metric.State, fingerprint));
        }
        return new(AlgorithmVersion, fingerprint, frozen, Array.AsReadOnly(windows.ToArray()));
    }
}
