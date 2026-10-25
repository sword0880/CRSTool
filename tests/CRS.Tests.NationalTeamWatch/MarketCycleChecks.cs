using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;

internal static class MarketCycleChecks
{
    private sealed record Fixture(DateOnly[] Days, MarketCycleIndexPoint[] IndexPoints,
        MarketCycleActivityPoint[] ActivityPoints, MarketCycleBreadthPoint[] BreadthPoints);

    private sealed class ApprovalEvidenceVerifier : IMarketCycleSourceApprovalEvidenceVerifier
    {
        private readonly HashSet<string> verified;
        public ApprovalEvidenceVerifier(params string[] hashes) =>
            verified = new HashSet<string>(hashes, StringComparer.OrdinalIgnoreCase);
        public bool Verify(string sha256) => verified.Contains(sha256);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS 市场周期：" + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS 市场周期：" + name); return; }
        throw new Exception(name);
    }

    private static DateTimeOffset Beijing(DateOnly date, int hour, int minute = 0) =>
        new(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(8));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static MarketCycleIndexPoint IndexPoint(DateOnly date, int index, decimal close,
        MarketCycleObservationState state = MarketCycleObservationState.Accepted,
        DateTimeOffset? published = null, DateTimeOffset? firstSeen = null, DateTimeOffset? retrieved = null,
        string revision = "r1") =>
        new(date, close, published ?? Beijing(date, 17), firstSeen ?? Beijing(date, 18),
            retrieved ?? Beijing(date, 18, 5), $"{date:yyyyMMdd}-{revision}",
            Hash($"index|{date:yyyy-MM-dd}|{index}|{close}|{revision}"), state);

    private static MarketCycleActivityPoint ActivityPoint(DateOnly date, int index, decimal? amount, decimal? volume,
        MarketCycleObservationState state = MarketCycleObservationState.Accepted,
        DateTimeOffset? published = null, DateTimeOffset? firstSeen = null, DateTimeOffset? retrieved = null,
        string revision = "r1") =>
        new(date, amount, volume, published ?? Beijing(date, 17), firstSeen ?? Beijing(date, 18),
            retrieved ?? Beijing(date, 18, 5), $"{date:yyyyMMdd}-{revision}",
            Hash($"activity|{date:yyyy-MM-dd}|{index}|{amount}|{volume}|{revision}"), state);

    private static MarketCycleBreadthPoint BreadthPoint(DateOnly date, int index, int above20, int above60,
        int newLow, int universe = 100, MarketCycleObservationState state = MarketCycleObservationState.Accepted,
        DateTimeOffset? published = null, DateTimeOffset? firstSeen = null, DateTimeOffset? retrieved = null,
        string revision = "r1") =>
        new(date, above20, above60, newLow, universe,
            $"{date:yyyyMMdd}-universe", Hash($"universe|{date:yyyy-MM-dd}|{universe}"), "synthetic-universe-v1",
            published ?? Beijing(date, 17), firstSeen ?? Beijing(date, 18), retrieved ?? Beijing(date, 18, 5),
            $"{date:yyyyMMdd}-{revision}",
            Hash($"breadth|{date:yyyy-MM-dd}|{index}|{above20}|{above60}|{newLow}|{universe}|{revision}"), state);

    private static Fixture Series(int count,
        Func<int, decimal>? close = null,
        Func<int, decimal?>? amount = null,
        Func<int, decimal?>? volume = null,
        Func<int, int>? above20 = null,
        Func<int, int>? above60 = null,
        Func<int, int>? newLow = null)
    {
        var start = new DateOnly(2025, 1, 1);
        var days = Enumerable.Range(0, count).Select(i => start.AddDays(i)).ToArray();
        var indexes = days.Select((day, i) => IndexPoint(day, i, close?.Invoke(i) ?? 100m + i)).ToArray();
        var activities = days.Select((day, i) => ActivityPoint(day, i, amount?.Invoke(i) ?? 1000m,
            volume?.Invoke(i) ?? 1000m)).ToArray();
        var breadth = days.Select((day, i) => BreadthPoint(day, i, above20?.Invoke(i) ?? 70,
            above60?.Invoke(i) ?? 60, newLow?.Invoke(i) ?? 5)).ToArray();
        return new(days, indexes, activities, breadth);
    }

    private static MarketCycleInput Input(Fixture fixture, bool calendarVerified = true) =>
        Input(fixture.Days, fixture.IndexPoints, fixture.ActivityPoints, fixture.BreadthPoints, calendarVerified);

    private static MarketCycleInput Input(DateOnly[] days, MarketCycleIndexPoint[] indexPoints,
        MarketCycleActivityPoint[] activityPoints, MarketCycleBreadthPoint[] breadthPoints,
        bool calendarVerified = true)
    {
        var asOf = days[^1];
        return new("CN", "CSI300", asOf, Beijing(asOf.AddDays(1), 9), calendarVerified,
            days, indexPoints, activityPoints, breadthPoints);
    }

    private static void RejectOperation(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS 市场周期来源门禁：" + name); return; }
        throw new Exception(name);
    }

    public static void Run()
    {
        var baseline = Series(260);
        var input = Input(baseline);
        var result = MarketCycleEvaluator.Evaluate(input);
        Check(result.DataStatus == MarketCycleDataStatus.ResearchPreview,
            "250 日有效指数收盘、独立成交与广度事实完整时仅输出研究预览");
        Check(result.ExpectedPriceSessions == 250 && result.AcceptedPriceSessions == 250,
            "250 日指数窗口和接受事实计数准确");
        Check(result.RollingHighClose == 359m && result.DrawdownFromRollingHigh == 0m,
            "滚动高点与回撤使用指数收盘价计算");
        Check(result.Prior20SessionMeanAmountYuan == 1000m && result.AmountRatioToPrior20Mean == 1m &&
            result.Prior20SessionMeanVolumeShares == 1000m && result.VolumeRatioToPrior20Mean == 1m,
            "成交额与成交量分别使用前 20 日基准且排除当日");
        Check(result.Return5Sessions == 359m / 354m - 1m, "5 日收益使用五个交易日间隔");
        Check(result.TopRiskScore == 1 && result.TopRiskLevel == MarketCycleSignalLevel.Watch,
            "接近阶段高位仅形成观察，不单独升级风险");
        Check(result.BottomCandidateScore == 0 && result.BottomCandidateLevel == MarketCycleSignalLevel.None,
            "普通上行样本不触发底部候选");
        Check(result.Fingerprint == MarketCycleEvaluator.Evaluate(input).Fingerprint,
            "相同输入与规则产生稳定指纹");

        var top = Series(260,
            close: i => i == 259 ? 358m : 100m + i,
            amount: i => i == 259 ? 2000m : 1000m,
            above20: i => i == 259 ? 30 : 60,
            above60: i => i == 259 ? 40 : 60);
        var topResult = MarketCycleEvaluator.Evaluate(Input(top));
        Check(topResult.TopRiskScore == 3 && topResult.TopRiskLevel == MarketCycleSignalLevel.Elevated,
            "顶部风险由价格位置、广度、量价三个证据族分别触发");
        Check(topResult.TopEvidenceFamilies.Count == 3, "同一证据族内多项广度条件只计一分");

        var volumeOnlySpike = Series(260,
            close: i => i == 259 ? 358m : 100m + i,
            amount: _ => 1000m,
            volume: i => i == 259 ? 2000m : 1000m,
            above20: i => i == 259 ? 30 : 60,
            above60: i => i == 259 ? 40 : 60);
        var volumeOnlyResult = MarketCycleEvaluator.Evaluate(Input(volumeOnlySpike));
        Check(volumeOnlyResult.AmountRatioToPrior20Mean == 1m && volumeOnlyResult.VolumeRatioToPrior20Mean == 2m &&
            volumeOnlyResult.TopEvidenceFamilies.Any(x => x.Contains("量价/流动性族", StringComparison.Ordinal)),
            "仅成交量放大也可触发量价族，但不会冒充成交额放大");

        decimal BottomClose(int i)
        {
            if (i <= 229) return 100m + i;
            if (i <= 258) return 329m - (i - 229) * (69m / 29m);
            return 250m;
        }
        var bottom = Series(260,
            close: BottomClose,
            amount: i => i == 259 ? 2000m : 1000m,
            above20: i => i >= 259 ? 10 : 60,
            above60: i => i >= 259 ? 15 : 60,
            newLow: i => i == 259 ? 30 : 5);
        var bottomResult = MarketCycleEvaluator.Evaluate(Input(bottom));
        Check(bottomResult.BottomCandidateScore == 3 && bottomResult.BottomCandidateLevel == MarketCycleSignalLevel.Candidate,
            "深度回撤、极弱广度与恐慌量价形成底部候选");
        Check(bottomResult.TopRiskLevel == MarketCycleSignalLevel.Watch,
            "深跌恐慌不会仅因放量而同时升级为顶部风险");
        Check(!bottomResult.RecoveryConfirmation, "深跌样本未出现修复条件时不标记修复");

        decimal DeepButNotNearHigh(int i)
        {
            if (i <= 229) return 100m + i;
            if (i <= 258) return 329m - (i - 229) * (76.5m / 29m);
            return 250m;
        }
        var deepButNotNearHigh = Series(260,
            close: DeepButNotNearHigh,
            amount: i => i == 259 ? 2000m : 1000m,
            above20: i => i >= 259 ? 10 : 60,
            above60: i => i >= 259 ? 15 : 60,
            newLow: i => i == 259 ? 30 : 5);
        var deepButNotNearHighResult = MarketCycleEvaluator.Evaluate(Input(deepButNotNearHigh));
        Check(deepButNotNearHighResult.TopRiskScore == 2 &&
            deepButNotNearHighResult.TopRiskLevel == MarketCycleSignalLevel.Watch,
            "非高位的广度恶化与放量不涨不能单独升级为顶部风险");
        Check(deepButNotNearHighResult.BottomCandidateLevel == MarketCycleSignalLevel.Candidate,
            "深度回撤并有独立佐证时可形成底部候选");

        var shallowPanic = Series(260,
            close: i => i == 259 ? 346m : 100m + i,
            amount: i => i == 259 ? 2000m : 1000m,
            above20: i => i == 259 ? 10 : 60,
            above60: i => i == 259 ? 15 : 60,
            newLow: i => i == 259 ? 30 : 5);
        var shallowPanicResult = MarketCycleEvaluator.Evaluate(Input(shallowPanic));
        Check(shallowPanicResult.BottomCandidateScore == 2 &&
            shallowPanicResult.BottomCandidateLevel == MarketCycleSignalLevel.Watch,
            "浅回撤的放量下跌不直接升级为底部候选");

        decimal RecoveryClose(int i)
        {
            if (i <= 229) return 100m + i;
            if (i <= 253) return 329m - (i - 229) * 4m;
            return i switch { 254 => 240m, 255 => 242m, 256 => 244m, 257 => 246m, 258 => 248m, _ => 250m };
        }
        var recovery = Series(260,
            close: RecoveryClose,
            amount: _ => 1000m,
            above20: i => i switch { 254 => 5, 255 => 8, 256 => 10, 257 => 12, 258 => 15, 259 => 18, _ => 50 },
            above60: i => i == 259 ? 15 : 40,
            newLow: i => i == 259 ? 10 : 20);
        var recoveryResult = MarketCycleEvaluator.Evaluate(Input(recovery));
        Check(recoveryResult.Return5Sessions == 250m / 240m - 1m, "修复判断收益窗口不使用未来收盘");
        Check(recoveryResult.BottomCandidateLevel == MarketCycleSignalLevel.Candidate &&
            recoveryResult.RecoveryConfirmation, "底部候选与后续修复条件分开输出");

        var missingIndex = baseline.IndexPoints.Where(x => x.Date != baseline.Days[100]).ToArray();
        var missingResult = MarketCycleEvaluator.Evaluate(Input(baseline.Days, missingIndex,
            baseline.ActivityPoints, baseline.BreadthPoints));
        Check(missingResult.DataStatus == MarketCycleDataStatus.InsufficientData &&
            missingResult.TopRiskLevel == MarketCycleSignalLevel.Unavailable &&
            missingResult.DataIssues.Any(x => x.Contains("缺少截止时点可见的指数", StringComparison.Ordinal)),
            "指数历史缺日使信号整体不可用，不跳过缺口");

        var missingAmount = baseline.ActivityPoints.Select((x, i) => i == 258 ? x with { AmountYuan = null } : x).ToArray();
        Check(MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints, missingAmount,
            baseline.BreadthPoints)).DataStatus == MarketCycleDataStatus.InsufficientData,
            "市场成交额基准窗口缺值阻断完整周期信号");

        var missingVolume = baseline.ActivityPoints.Select((x, i) => i == 258 ? x with { VolumeShares = null } : x).ToArray();
        Check(MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints, missingVolume,
            baseline.BreadthPoints)).DataStatus == MarketCycleDataStatus.InsufficientData,
            "市场成交量基准窗口缺值阻断完整周期信号");

        var needsReview = baseline.IndexPoints.Select((x, i) => i == 259
            ? x with { State = MarketCycleObservationState.NeedsReview } : x).ToArray();
        Check(MarketCycleEvaluator.Evaluate(Input(baseline.Days, needsReview, baseline.ActivityPoints,
            baseline.BreadthPoints)).DataStatus == MarketCycleDataStatus.InsufficientData,
            "待审核的指数事实不能产生周期信号");

        var duplicateIndex = baseline.IndexPoints.Append(baseline.IndexPoints[^1] with
        {
            RevisionId = baseline.IndexPoints[^1].RevisionId + "-conflict",
            EvidenceHash = Hash("conflicting-index-revision")
        }).ToArray();
        var duplicateIndexResult = MarketCycleEvaluator.Evaluate(Input(baseline.Days, duplicateIndex,
            baseline.ActivityPoints, baseline.BreadthPoints));
        Check(duplicateIndexResult.DataStatus == MarketCycleDataStatus.InsufficientData &&
            duplicateIndexResult.DataIssues.Any(x => x.Contains("指数", StringComparison.Ordinal) &&
                x.Contains("多个评估截止时点可见的修订", StringComparison.Ordinal)),
            "同日指数修订冲突阻断评估，不按数据库顺序选值");

        var duplicateActivity = baseline.ActivityPoints.Append(baseline.ActivityPoints[^1] with
        {
            RevisionId = baseline.ActivityPoints[^1].RevisionId + "-conflict",
            EvidenceHash = Hash("conflicting-activity-revision")
        }).ToArray();
        Check(MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints, duplicateActivity,
            baseline.BreadthPoints)).DataStatus == MarketCycleDataStatus.InsufficientData,
            "市场成交修订冲突独立阻断评估");

        var duplicateBreadth = baseline.BreadthPoints.Append(baseline.BreadthPoints[^1] with
        {
            RevisionId = baseline.BreadthPoints[^1].RevisionId + "-conflict",
            EvidenceHash = Hash("conflicting-breadth-revision")
        }).ToArray();
        Check(MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints, baseline.ActivityPoints,
            duplicateBreadth)).DataStatus == MarketCycleDataStatus.InsufficientData,
            "市场广度修订冲突独立阻断评估");

        var cutoff = input.EvaluationCutoff;
        var futureIndexRevision = baseline.IndexPoints[^1] with
        {
            Close = 9999m,
            RevisionId = "future-correction",
            EvidenceHash = Hash("future-index-correction"),
            SourcePublishedAt = cutoff.AddMinutes(1),
            FirstSeenAt = cutoff.AddMinutes(2),
            RetrievedAt = cutoff.AddMinutes(3)
        };
        var pointInTime = MarketCycleEvaluator.Evaluate(Input(baseline.Days,
            baseline.IndexPoints.Append(futureIndexRevision).ToArray(), baseline.ActivityPoints, baseline.BreadthPoints));
        Check(pointInTime.DataStatus == MarketCycleDataStatus.ResearchPreview &&
            pointInTime.Fingerprint == result.Fingerprint &&
            pointInTime.RollingHighClose == result.RollingHighClose,
            "截止时间之后的指数修订不会污染历史结果或指纹");

        var unverifiedCalendar = MarketCycleEvaluator.Evaluate(Input(baseline, false));
        Check(unverifiedCalendar.DataStatus == MarketCycleDataStatus.InsufficientData &&
            unverifiedCalendar.TopRiskLevel == MarketCycleSignalLevel.Unavailable,
            "未核验交易日历不能产生周期信号");

        var duplicateCalendar = baseline.Days.Append(baseline.Days[^1]).ToArray();
        Reject(() => MarketCycleEvaluator.Evaluate(Input(duplicateCalendar, baseline.IndexPoints,
            baseline.ActivityPoints, baseline.BreadthPoints)), "重复交易日历拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(Input(baseline.Days,
            baseline.IndexPoints.Select((x, i) => i == 259 ? x with { EvidenceHash = "bad" } : x).ToArray(),
            baseline.ActivityPoints, baseline.BreadthPoints)), "指数原件哈希格式错误拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints,
            baseline.ActivityPoints.Select((x, i) => i == 259 ? x with { AmountYuan = -1m } : x).ToArray(),
            baseline.BreadthPoints)), "负市场成交额拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints,
            baseline.ActivityPoints.Select((x, i) => i == 259 ? x with { VolumeShares = -1m } : x).ToArray(),
            baseline.BreadthPoints)), "负市场成交量拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints,
            baseline.ActivityPoints, baseline.BreadthPoints.Select((x, i) =>
                i == 259 ? x with { AboveMa20Count = 101 } : x).ToArray())), "广度计数超过证券宇宙分母拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(Input(baseline.Days, baseline.IndexPoints,
            baseline.ActivityPoints, baseline.BreadthPoints.Select((x, i) =>
                i == 259 ? x with { UniverseSnapshotHash = "bad" } : x).ToArray())),
            "广度证券宇宙快照哈希缺失或格式错误拒绝");
        Reject(() => MarketCycleEvaluator.Evaluate(input, new MarketCycleRules(TopNearHighRatio: 1.1m)),
            "非法候选阈值拒绝");

        var approvalAt = input.EvaluationCutoff.AddHours(-1);
        var approvedSource = new MarketCycleSourceApproval(
            "sse-index-history",
            new Uri("https://english.sse.com.cn/markets/dataservice/products/"),
            "IndexDaily", "contract-v1", "csv-parser-v1",
            MarketCycleSourceApprovalStatus.Approved,
            MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage |
                MarketCycleSourceUse.HistoricalBackfill | MarketCycleSourceUse.Redistribution,
            Hash("reviewed license terms"), "fixture-reviewer", approvalAt, "source-policy-v1",
            input.EvaluationCutoff.AddDays(1));
        var approvalEvidence = new ApprovalEvidenceVerifier(approvedSource.TermsEvidenceHash);
        MarketCycleSourceAdmission.Require(approvedSource, MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence);
        MarketCycleSourceAdmission.Require(approvedSource, MarketCycleSourceOperation.HistoricalBackfill, input.EvaluationCutoff, approvalEvidence);
        MarketCycleSourceAdmission.Require(approvedSource, MarketCycleSourceOperation.Redistribution, input.EvaluationCutoff, approvalEvidence);
        Check(true, "显式批准的用途在有效期内通过来源门禁");

        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { Status = MarketCycleSourceApprovalStatus.Pending },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "待审核来源不允许自动采集");
        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ApprovedUses = MarketCycleSourceUse.AutomatedCollection },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "仅批准采集但未批准持久化仍被拒绝");
        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ApprovedUses = MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage },
            MarketCycleSourceOperation.HistoricalBackfill, input.EvaluationCutoff, approvalEvidence),
            "自动采集权限不能代替历史回填权限");
        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ApprovedUses = MarketCycleSourceUse.AutomatedCollection | MarketCycleSourceUse.PersistentStorage },
            MarketCycleSourceOperation.Redistribution, input.EvaluationCutoff, approvalEvidence),
            "本地存储权限不能代替再分发权限");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { TermsEvidenceHash = "bad" },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "来源许可证据哈希格式错误拒绝");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { TermsEvidenceHash = Hash("unarchived license terms") },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff,
            new ApprovalEvidenceVerifier()),
            "仅有哈希格式但没有可核验条款原件仍被拒绝");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ReviewedAt = input.EvaluationCutoff.AddMinutes(1) },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "未来才完成的审批不能用于过去的执行时点");
        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ValidUntil = input.EvaluationCutoff },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "过期来源审批拒绝");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { SourceUri = new Uri("http://example.com/data") },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "非 HTTPS 来源地址拒绝");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { SourceUri = new Uri("https://example.com/data?token=secret") },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "来源基址包含查询参数/潜在凭据时拒绝");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { Reviewer = " " },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "缺少人工审核人时拒绝");
        RejectOperation(() => MarketCycleSourceAdmission.Require(
            approvedSource with { Status = MarketCycleSourceApprovalStatus.Rejected },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "已拒绝的来源审批不能执行");
        Reject(() => MarketCycleSourceAdmission.Require(
            approvedSource with { ApprovedUses = (MarketCycleSourceUse)128 },
            MarketCycleSourceOperation.AutomatedPolling, input.EvaluationCutoff, approvalEvidence),
            "未知来源权限位拒绝");

        Console.WriteLine("市场周期研究检查完成。");
    }
}
