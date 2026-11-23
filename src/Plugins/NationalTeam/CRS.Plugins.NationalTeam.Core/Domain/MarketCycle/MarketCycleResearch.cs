using System.Security.Cryptography;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>原始市场事实的审核状态；只有 Accepted 可以参与完整周期评估。</summary>
public enum MarketCycleObservationState { Accepted, NeedsReview, Critical, Rejected }

/// <summary>ResearchPreview 仅表示输入完整且计算可复现，不表示模型具有预测能力或获准生产。</summary>
public enum MarketCycleDataStatus { InsufficientData, ResearchPreview }

/// <summary>周期状态是条件计数，不是概率；Unavailable 表示关键输入门槛未通过。</summary>
public enum MarketCycleSignalLevel { Unavailable, None, Watch, Elevated, Candidate }

/// <summary>基准指数收盘事实，只承载指数价格来源自己的修订、时间戳和原件哈希。</summary>
public sealed record MarketCycleIndexPoint(
    DateOnly Date, decimal Close, DateTimeOffset? SourcePublishedAt, DateTimeOffset FirstSeenAt,
    DateTimeOffset RetrievedAt, string RevisionId, string EvidenceHash, MarketCycleObservationState State);

/// <summary>全市场成交事实，与指数价格和市场广度分开存储；金额为人民币元、成交量为原始股数。</summary>
public sealed record MarketCycleActivityPoint(
    DateOnly Date, decimal? AmountYuan, decimal? VolumeShares, DateTimeOffset? SourcePublishedAt,
    DateTimeOffset FirstSeenAt, DateTimeOffset RetrievedAt, string RevisionId, string EvidenceHash,
    MarketCycleObservationState State);

/// <summary>
/// 市场广度事实独立于成交统计。UniverseCount 必须代表当日实际纳入统计的证券宇宙，
/// 不能用今天仍上市的证券名单回填历史比例。
/// </summary>
public sealed record MarketCycleBreadthPoint(
    DateOnly Date, int? AboveMa20Count, int? AboveMa60Count, int? NewLowCount, int? UniverseCount,
    string UniverseSnapshotId, string UniverseSnapshotHash, string UniverseDefinitionVersion,
    DateTimeOffset? SourcePublishedAt, DateTimeOffset FirstSeenAt, DateTimeOffset RetrievedAt,
    string RevisionId, string EvidenceHash, MarketCycleObservationState State);

/// <summary>不同粒度的事实独立传入；领域层不联网、不查询系统时钟，也不进行跨指数成交额累加。</summary>
public sealed record MarketCycleInput(
    string Market, string IndexCode, DateOnly AsOfDate, DateTimeOffset EvaluationCutoff,
    bool CalendarVerified, IReadOnlyList<DateOnly> TradingDays,
    IReadOnlyList<MarketCycleIndexPoint> IndexPoints,
    IReadOnlyList<MarketCycleActivityPoint> ActivityPoints,
    IReadOnlyList<MarketCycleBreadthPoint> BreadthPoints,string? CalendarRevisionId=null,string? CalendarEvidenceHash=null);

/// <summary>
/// 版本化研究阈值。默认参数仅为待回测的候选值，不能当作生产参数或投资建议。
/// 顶部和底部各按三个证据族计分，每个族最多一分。
/// </summary>
public sealed record MarketCycleRules(
    string Version = "cycle-candidate-v1.0",
    int PriceHistorySessions = 250,
    int TurnoverBaselineSessions = 20,
    int BreadthComparisonSessions = 5,
    decimal TopNearHighRatio = 0.95m,
    decimal TopWeakAboveMa20Ratio = 0.45m,
    decimal TopWeakAboveMa60Ratio = 0.55m,
    decimal BreadthDeterioration = 0.10m,
    decimal TurnoverSpikeRatio = 1.5m,
    decimal BottomDrawdown = 0.20m,
    decimal BottomOversoldAboveMa20Ratio = 0.20m,
    decimal BottomNewLowRatio = 0.20m,
    decimal PanicDailyReturn = -0.03m,
    decimal RecoveryReturn5Day = 0.03m,
    decimal RecoveryBreadthImprovement = 0.10m,
    int TopRiskWatchScore = 1,
    int TopRiskElevatedScore = 2,
    int BottomWatchScore = 1,
    int BottomCandidateScore = 2)
{
    public MarketCycleRules Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 40 ||
            Version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new InvalidDataException("市场周期规则版本无效。");
        if (PriceHistorySessions < 250 || TurnoverBaselineSessions != 20 || BreadthComparisonSessions != 5)
            throw new InvalidDataException("当前算法固定使用至少 250 日历史、前 20 日成交基准和 5 日广度比较；变更窗口须升级算法语义。");
        if (TopNearHighRatio is <= 0 or > 1 ||
            TopWeakAboveMa20Ratio is < 0 or > 1 ||
            TopWeakAboveMa60Ratio is < 0 or > 1 ||
            BreadthDeterioration is <= 0 or > 1 ||
            TurnoverSpikeRatio <= 0 ||
            BottomDrawdown is <= 0 or >= 1 ||
            BottomOversoldAboveMa20Ratio is < 0 or > 1 ||
            BottomNewLowRatio is < 0 or > 1 ||
            PanicDailyReturn is >= 0 or <= -1 ||
            RecoveryReturn5Day <= 0 ||
            RecoveryBreadthImprovement is <= 0 or > 1)
            throw new InvalidDataException("市场周期研究阈值超出有效范围。");
        if (TopRiskWatchScore is < 1 or > 3 || TopRiskElevatedScore < TopRiskWatchScore || TopRiskElevatedScore > 3 ||
            BottomWatchScore is < 1 or > 3 || BottomCandidateScore < BottomWatchScore || BottomCandidateScore > 3)
            throw new InvalidDataException("市场周期证据族评分门槛无效。");
        return this;
    }
}

public sealed record MarketCycleAssessment(
    string Market, string IndexCode, DateOnly AsOfDate, DateTimeOffset EvaluationCutoff, string RuleVersion,
    MarketCycleDataStatus DataStatus, int ExpectedPriceSessions, int AcceptedPriceSessions,
    decimal? RollingHighClose, decimal? DrawdownFromRollingHigh, decimal? DailyReturn, decimal? Return5Sessions,
    decimal? Prior20SessionMeanAmountYuan, decimal? AmountRatioToPrior20Mean,
    decimal? Prior20SessionMeanVolumeShares, decimal? VolumeRatioToPrior20Mean,
    decimal? AboveMa20Ratio, decimal? AboveMa60Ratio, decimal? NewLowRatio,
    int TopRiskScore, MarketCycleSignalLevel TopRiskLevel,
    int BottomCandidateScore, MarketCycleSignalLevel BottomCandidateLevel, bool RecoveryConfirmation,
    IReadOnlyList<string> TopEvidenceFamilies, IReadOnlyList<string> BottomEvidenceFamilies,
    IReadOnlyList<string> DataIssues, string Fingerprint)
{
    // 输入齐全只允许规则研究，不能被界面或导出解释为预测验证或具名机构成交证明。
    public string TimingProof=>"NotProven";
    public string InstitutionTradingEvidence=>"NotObserved";
}

/// <summary>
/// 纯函数式、点时可复现的周期候选计算。指数价格、市场成交额/量、市场广度分别验收。
/// 关键输入缺失、待审或冲突时关闭全部信号；不对国家队交易作归因，也不输出未经校准的概率。
/// </summary>
public static class MarketCycleEvaluator
{
    public static MarketCycleAssessment Evaluate(MarketCycleInput input, MarketCycleRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TradingDays);
        ArgumentNullException.ThrowIfNull(input.IndexPoints);
        ArgumentNullException.ThrowIfNull(input.ActivityPoints);
        ArgumentNullException.ThrowIfNull(input.BreadthPoints);
        rules = (rules ?? new MarketCycleRules()).Validate();

        if (string.IsNullOrWhiteSpace(input.Market) || string.IsNullOrWhiteSpace(input.IndexCode))
            throw new InvalidDataException("市场与基准指数标识不能为空。");
        // 日事实不能用更早的知识截止研究未来收盘；默认时间也不能冒充实际观测。
        if(input.EvaluationCutoff==default||input.AsOfDate==default||input.AsOfDate>DateOnly.FromDateTime(input.EvaluationCutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("市场周期评估日期超过知识截止或时间身份缺失。");

        var calendar = input.TradingDays.Order().ToArray();
        if (calendar.Distinct().Count() != calendar.Length)
            throw new InvalidDataException("交易日历存在重复日期，拒绝计算市场周期。");

        var issues = new List<string>();
        if (!input.CalendarVerified) issues.Add("交易日历尚未通过核验。");
        if(string.IsNullOrWhiteSpace(input.CalendarRevisionId)||input.CalendarEvidenceHash is not {Length:64}||!input.CalendarEvidenceHash.All(Uri.IsHexDigit))
            issues.Add("交易日历缺少绑定的修订身份或原件指纹，不能仅凭核验标志计算信号。");
        if (!calendar.Contains(input.AsOfDate)) issues.Add("评估日期不在已登记交易日历中。");

        var expectedDays = calendar.Where(x => x <= input.AsOfDate)
            .TakeLast(rules.PriceHistorySessions).ToArray();
        if (expectedDays.Length < rules.PriceHistorySessions)
            issues.Add($"有效交易日历史不足：需要 {rules.PriceHistorySessions} 日，当前日历仅有 {expectedDays.Length} 日。");

        var from = expectedDays.Length == 0 ? input.AsOfDate : expectedDays[0];
        var activityDays = expectedDays.TakeLast(rules.TurnoverBaselineSessions + 1).ToArray();
        var breadthDays = expectedDays.TakeLast(rules.BreadthComparisonSessions + 1).ToArray();
        var activityFrom = activityDays.Length == 0 ? input.AsOfDate : activityDays[0];
        var breadthFrom = breadthDays.Length == 0 ? input.AsOfDate : breadthDays[0];

        var visibleIndex = input.IndexPoints
            .Where(x => x.Date >= from && x.Date <= input.AsOfDate)
            .Where(x => IsVisible(x.SourcePublishedAt, x.FirstSeenAt, x.RetrievedAt, input.EvaluationCutoff))
            .OrderBy(x => x.Date).ThenBy(x => x.RevisionId, StringComparer.Ordinal).ToArray();
        var visibleActivity = input.ActivityPoints
            .Where(x => x.Date >= activityFrom && x.Date <= input.AsOfDate)
            .Where(x => IsVisible(x.SourcePublishedAt, x.FirstSeenAt, x.RetrievedAt, input.EvaluationCutoff))
            .OrderBy(x => x.Date).ThenBy(x => x.RevisionId, StringComparer.Ordinal).ToArray();
        var visibleBreadth = input.BreadthPoints
            .Where(x => x.Date >= breadthFrom && x.Date <= input.AsOfDate)
            .Where(x => IsVisible(x.SourcePublishedAt, x.FirstSeenAt, x.RetrievedAt, input.EvaluationCutoff))
            .OrderBy(x => x.Date).ThenBy(x => x.RevisionId, StringComparer.Ordinal).ToArray();

        foreach (var point in visibleIndex) ValidateIndexPoint(point);
        foreach (var point in visibleActivity) ValidateActivityPoint(point);
        foreach (var point in visibleBreadth) ValidateBreadthPoint(point);

        if (visibleIndex.Any(x => !calendar.Contains(x.Date)))
            issues.Add("目标价格区间存在不属于已登记交易日历的指数来源记录。");
        if (visibleActivity.Any(x => !calendar.Contains(x.Date)))
            issues.Add("目标成交区间存在不属于已登记交易日历的市场成交来源记录。");
        if (visibleBreadth.Any(x => !calendar.Contains(x.Date)))
            issues.Add("目标广度区间存在不属于已登记交易日历的市场广度来源记录。");

        var indexGroups = visibleIndex.Where(x => calendar.Contains(x.Date))
            .GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToArray());
        var activityGroups = visibleActivity.Where(x => calendar.Contains(x.Date))
            .GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToArray());
        var breadthGroups = visibleBreadth.Where(x => calendar.Contains(x.Date))
            .GroupBy(x => x.Date).ToDictionary(x => x.Key, x => x.ToArray());

        AddDuplicateIssues(indexGroups, issues, "指数");
        AddDuplicateIssues(activityGroups, issues, "市场成交");
        AddDuplicateIssues(breadthGroups, issues, "市场广度");

        var acceptedIndex = SelectAccepted(expectedDays, indexGroups, issues, "指数收盘", x => x.State);
        var acceptedActivity = SelectAccepted(activityDays, activityGroups, issues, "市场成交额/量", x => x.State);
        var acceptedBreadth = SelectAccepted(breadthDays, breadthGroups, issues, "市场广度", x => x.State);

        var acceptedCount = acceptedIndex.Count;
        var history = expectedDays.Where(acceptedIndex.ContainsKey).Select(day => acceptedIndex[day]).ToArray();
        var activityHistory = activityDays.Where(acceptedActivity.ContainsKey).Select(day => acceptedActivity[day]).ToArray();
        var breadthHistory = breadthDays.Where(acceptedBreadth.ContainsKey).Select(day => acceptedBreadth[day]).ToArray();

        if (activityHistory.Length == activityDays.Length && activityDays.Length == rules.TurnoverBaselineSessions + 1)
        {
            if (activityHistory.Any(x => x.AmountYuan is null || x.VolumeShares is null))
                issues.Add($"最近 {rules.TurnoverBaselineSessions + 1} 个有效交易日成交额或成交量不完整。");
            if (activityHistory.All(x => x.AmountYuan.HasValue) &&
                activityHistory.Take(rules.TurnoverBaselineSessions).All(x => x.AmountYuan.HasValue) &&
                activityHistory.Take(rules.TurnoverBaselineSessions).Average(x => x.AmountYuan!.Value) <= 0)
                issues.Add("前置成交额基准均值不大于零，无法计算放量比率。");
            if (activityHistory.All(x => x.VolumeShares.HasValue) &&
                activityHistory.Take(rules.TurnoverBaselineSessions).All(x => x.VolumeShares.HasValue) &&
                activityHistory.Take(rules.TurnoverBaselineSessions).Average(x => x.VolumeShares!.Value) <= 0)
                issues.Add("前置成交量基准均值不大于零，无法计算放量比率。");
        }
        else if (activityDays.Length < rules.TurnoverBaselineSessions + 1)
            issues.Add("市场级成交额/量历史不足以计算前 20 日基准。");

        if (breadthHistory.Length == breadthDays.Length && breadthDays.Length == rules.BreadthComparisonSessions + 1)
        {
            if(breadthHistory.Select(x=>x.UniverseDefinitionVersion).Distinct(StringComparer.Ordinal).Count()>1)
                issues.Add("市场广度比较窗口的统计定义版本变化，不能直接比较比例。");
            ValidateBreadthAvailable(breadthHistory[^1], issues, "评估日");
            ValidateBreadthAvailable(breadthHistory[breadthHistory.Length - 1 - rules.BreadthComparisonSessions], issues, "广度比较日");
        }
        else if (breadthDays.Length < rules.BreadthComparisonSessions + 1)
            issues.Add("市场广度历史不足以完成 5 日比较。");

        var fingerprint = CreateFingerprint(input, rules, visibleIndex, visibleActivity, visibleBreadth,
            expectedDays, activityDays, breadthDays);
        if (issues.Count > 0 || history.Length != rules.PriceHistorySessions ||
            activityHistory.Length != rules.TurnoverBaselineSessions + 1 ||
            breadthHistory.Length != rules.BreadthComparisonSessions + 1)
        {
            return Unavailable(input, rules, acceptedCount,
                issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), fingerprint);
        }

        var latest = history[^1];
        var prior = history[^2];
        var latestActivity = activityHistory[^1];
        var latestBreadth = breadthHistory[^1];
        var breadthPast = breadthHistory[breadthHistory.Length - 1 - rules.BreadthComparisonSessions];
        var rollingHigh = history.TakeLast(250).Max(x => x.Close);
        var drawdown = latest.Close / rollingHigh - 1m;
        var dailyReturn = latest.Close / prior.Close - 1m;
        var return5 = latest.Close / history[history.Length - 1 - 5].Close - 1m;

        var priorMeanAmount = activityHistory.Take(rules.TurnoverBaselineSessions)
            .Average(x => x.AmountYuan!.Value);
        var amountRatio = latestActivity.AmountYuan!.Value / priorMeanAmount;
        var priorMeanVolume = activityHistory.Take(rules.TurnoverBaselineSessions)
            .Average(x => x.VolumeShares!.Value);
        var volumeRatio = latestActivity.VolumeShares!.Value / priorMeanVolume;

        var above20 = Ratio(latestBreadth.AboveMa20Count!.Value, latestBreadth.UniverseCount!.Value);
        var above60 = Ratio(latestBreadth.AboveMa60Count!.Value, latestBreadth.UniverseCount!.Value);
        var above20Past = Ratio(breadthPast.AboveMa20Count!.Value, breadthPast.UniverseCount!.Value);
        var newLowRatio = Ratio(latestBreadth.NewLowCount!.Value, latestBreadth.UniverseCount!.Value);
        var breadthDeteriorated = above20Past - above20 >= rules.BreadthDeterioration;
        var nearHigh = latest.Close >= rollingHigh * rules.TopNearHighRatio;
        var deepDrawdown = drawdown <= -rules.BottomDrawdown;
        var activitySpike = amountRatio >= rules.TurnoverSpikeRatio || volumeRatio >= rules.TurnoverSpikeRatio;

        var topFamilies = new List<string>();
        if (nearHigh)
            topFamilies.Add("价格位置族：接近 250 日最高收盘。");
        if (above20 <= rules.TopWeakAboveMa20Ratio ||
            above60 <= rules.TopWeakAboveMa60Ratio ||
            breadthDeteriorated)
            topFamilies.Add("市场广度族：均线广度偏弱或 5 日广度恶化。");
        // 避免将深跌恐慌同时解释为顶部放量滞涨；大跌归入底部恐慌量价族。
        if (activitySpike && dailyReturn <= 0 && dailyReturn > rules.PanicDailyReturn)
            topFamilies.Add("量价/流动性族：成交额或成交量放大、收盘不涨但未达到恐慌跌幅。");

        var bottomFamilies = new List<string>();
        if (deepDrawdown)
            bottomFamilies.Add("阶段回撤族：相对 250 日最高收盘显著回撤。");
        if (above20 <= rules.BottomOversoldAboveMa20Ratio ||
            newLowRatio >= rules.BottomNewLowRatio)
            bottomFamilies.Add("市场广度压力族：均线广度极弱或创新低比例极高。");
        if (activitySpike && dailyReturn <= rules.PanicDailyReturn)
            bottomFamilies.Add("恐慌量价族：成交额或成交量放大且当日跌幅较大。");

        var topScore = topFamilies.Count;
        var bottomScore = bottomFamilies.Count;
        // 顶部“风险升高”必须处于 250 日高位附近并至少有一个独立佐证；
        // 底部“候选”必须先满足深度回撤，再由其他证据族佐证。
        var topLevel = nearHigh && topScore >= rules.TopRiskElevatedScore ? MarketCycleSignalLevel.Elevated :
            topScore >= rules.TopRiskWatchScore ? MarketCycleSignalLevel.Watch : MarketCycleSignalLevel.None;
        var bottomLevel = deepDrawdown && bottomScore >= rules.BottomCandidateScore ? MarketCycleSignalLevel.Candidate :
            bottomScore >= rules.BottomWatchScore ? MarketCycleSignalLevel.Watch : MarketCycleSignalLevel.None;
        var recovery = return5 >= rules.RecoveryReturn5Day &&
            above20 - above20Past >= rules.RecoveryBreadthImprovement;

        return new(input.Market, input.IndexCode, input.AsOfDate, input.EvaluationCutoff, rules.Version,
            MarketCycleDataStatus.ResearchPreview, rules.PriceHistorySessions, acceptedCount,
            rollingHigh, drawdown, dailyReturn, return5, priorMeanAmount, amountRatio, priorMeanVolume, volumeRatio,
            above20, above60, newLowRatio, topScore, topLevel, bottomScore, bottomLevel, recovery,
            topFamilies, bottomFamilies, [], fingerprint);
    }

    private static bool IsVisible(DateTimeOffset? publishedAt, DateTimeOffset firstSeenAt,
        DateTimeOffset retrievedAt, DateTimeOffset cutoff) =>
        firstSeenAt <= cutoff && retrievedAt <= cutoff && (publishedAt is null || publishedAt <= cutoff);

    private static Dictionary<DateOnly, T> SelectAccepted<T>(IReadOnlyList<DateOnly> days,
        IReadOnlyDictionary<DateOnly, T[]> groups, List<string> issues, string label,
        Func<T, MarketCycleObservationState> state)
    {
        var accepted = new Dictionary<DateOnly, T>();
        foreach (var day in days)
        {
            if (!groups.TryGetValue(day, out var rows))
            {
                issues.Add($"{day:yyyy-MM-dd} 缺少截止时点可见的{label}事实。");
                continue;
            }
            if (rows.Length != 1) continue;
            if (state(rows[0]) != MarketCycleObservationState.Accepted)
            {
                issues.Add($"{label} {day:yyyy-MM-dd} 当前可见事实状态为 {state(rows[0])}，未通过数据审核。");
                continue;
            }
            accepted.Add(day, rows[0]);
        }
        return accepted;
    }

    private static void AddDuplicateIssues<T>(IReadOnlyDictionary<DateOnly, T[]> groups,
        List<string> issues, string label)
    {
        foreach (var duplicate in groups.Where(x => x.Value.Length > 1).OrderBy(x => x.Key))
            issues.Add($"{label} {duplicate.Key:yyyy-MM-dd} 存在多个评估截止时点可见的修订，无法唯一确定有效版本。");
    }

    private static void ValidateIndexPoint(MarketCycleIndexPoint value)
    {
        ValidateEvidence(value.RevisionId, value.EvidenceHash, value.SourcePublishedAt, value.FirstSeenAt, value.RetrievedAt);
        ValidateObservationDate(value.Date,value.RetrievedAt);
        if (value.Close <= 0)
            throw new InvalidDataException("指数收盘必须为正数。");
    }

    private static void ValidateActivityPoint(MarketCycleActivityPoint value)
    {
        ValidateEvidence(value.RevisionId, value.EvidenceHash, value.SourcePublishedAt, value.FirstSeenAt, value.RetrievedAt);
        ValidateObservationDate(value.Date,value.RetrievedAt);
        if (value.AmountYuan is < 0 || value.VolumeShares is < 0)
            throw new InvalidDataException("市场成交额或成交量不得为负数。");
    }

    private static void ValidateBreadthPoint(MarketCycleBreadthPoint value)
    {
        ValidateEvidence(value.RevisionId, value.EvidenceHash, value.SourcePublishedAt, value.FirstSeenAt, value.RetrievedAt);
        ValidateObservationDate(value.Date,value.RetrievedAt);
        if (string.IsNullOrWhiteSpace(value.UniverseSnapshotId) ||
            string.IsNullOrWhiteSpace(value.UniverseDefinitionVersion) ||
            string.IsNullOrWhiteSpace(value.UniverseSnapshotHash) ||
            value.UniverseSnapshotHash.Length != 64 || !value.UniverseSnapshotHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("市场广度缺少可追溯的证券宇宙快照标识、版本或哈希。");
        if (value.AboveMa20Count is < 0 || value.AboveMa60Count is < 0 ||
            value.NewLowCount is < 0 || value.UniverseCount is <= 0)
            throw new InvalidDataException("市场广度计数或证券宇宙规模无效。");
        if (value.UniverseCount is { } total &&
            (value.AboveMa20Count > total || value.AboveMa60Count > total || value.NewLowCount > total))
            throw new InvalidDataException("市场广度计数超过有效证券宇宙分母。");
    }

    private static void ValidateEvidence(string revisionId, string evidenceHash, DateTimeOffset? publishedAt,
        DateTimeOffset firstSeenAt, DateTimeOffset retrievedAt)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
            throw new InvalidDataException("可见市场事实缺少修订 ID。");
        if (string.IsNullOrWhiteSpace(evidenceHash) || evidenceHash.Length != 64 || !evidenceHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("可见市场事实缺少有效 SHA-256 原件哈希。");
        // FirstSeen为该修订正式登记时刻，Retrieved为其原件首次取得时刻；取得前不能先拥有金融事实。
        if (firstSeenAt==default||retrievedAt==default||publishedAt==default(DateTimeOffset)||retrievedAt > firstSeenAt ||
            publishedAt is { } published && (published > firstSeenAt || published > retrievedAt))
            throw new InvalidDataException("来源发布时间、首次发现时间与实际获取时间顺序不一致。");
    }

    private static void ValidateObservationDate(DateOnly date,DateTimeOffset retrievedAt)
    {
        if(date==default||date>DateOnly.FromDateTime(retrievedAt.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("市场日事实日期晚于其原件取得日期。");
    }

    private static void ValidateBreadthAvailable(MarketCycleBreadthPoint value, List<string> issues, string label)
    {
        if (value.UniverseCount is null or <= 0 || value.AboveMa20Count is null ||
            value.AboveMa60Count is null || value.NewLowCount is null)
            issues.Add($"{label}缺少经审核的均线广度、创新低计数或有效证券宇宙分母。");
    }

    private static decimal Ratio(int numerator, int denominator) => (decimal)numerator / denominator;

    private static MarketCycleAssessment Unavailable(MarketCycleInput input, MarketCycleRules rules,
        int accepted, IReadOnlyList<string> issues, string fingerprint) =>
        new(input.Market, input.IndexCode, input.AsOfDate, input.EvaluationCutoff, rules.Version,
            MarketCycleDataStatus.InsufficientData, rules.PriceHistorySessions, accepted,
            null, null, null, null, null, null, null, null, null, null, null, 0,
            MarketCycleSignalLevel.Unavailable, 0, MarketCycleSignalLevel.Unavailable, false,
            [], [], issues, fingerprint);

    private static string CreateFingerprint(MarketCycleInput input, MarketCycleRules rules,
        IReadOnlyList<MarketCycleIndexPoint> indexPoints, IReadOnlyList<MarketCycleActivityPoint> activityPoints,
        IReadOnlyList<MarketCycleBreadthPoint> breadthPoints, IReadOnlyList<DateOnly> expectedDays,
        IReadOnlyList<DateOnly> activityDays, IReadOnlyList<DateOnly> breadthDays)
    {
        // 结构化编码防止市场、指数或修订ID中的分隔符产生相同拼接串；同一时刻统一UTC。
        var canonical=new
        {
            InputContract="market-cycle-input-v2",Rules=rules,input.Market,input.IndexCode,input.AsOfDate,
            EvaluationCutoff=input.EvaluationCutoff.ToUniversalTime(),input.CalendarVerified,input.CalendarRevisionId,input.CalendarEvidenceHash,
            ExpectedDays=expectedDays,ActivityDays=activityDays,BreadthDays=breadthDays,
            Index=indexPoints.OrderBy(x=>x.Date).ThenBy(x=>x.RevisionId,StringComparer.Ordinal).Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt?.ToUniversalTime(),FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),RetrievedAt=x.RetrievedAt.ToUniversalTime()}).ToArray(),
            Activity=activityPoints.OrderBy(x=>x.Date).ThenBy(x=>x.RevisionId,StringComparer.Ordinal).Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt?.ToUniversalTime(),FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),RetrievedAt=x.RetrievedAt.ToUniversalTime()}).ToArray(),
            Breadth=breadthPoints.OrderBy(x=>x.Date).ThenBy(x=>x.RevisionId,StringComparer.Ordinal).Select(x=>x with{SourcePublishedAt=x.SourcePublishedAt?.ToUniversalTime(),FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),RetrievedAt=x.RetrievedAt.ToUniversalTime()}).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }
}
