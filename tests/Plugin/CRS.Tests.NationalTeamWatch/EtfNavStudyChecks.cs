using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

internal static class EtfNavStudyChecks
{
    private static readonly string Hash = new('a', 64);
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS 同日NAV研究：" + name); }
    private static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("未拒绝：" + name); }
    private static EtfNavStudyRequest Fixture()
    {
        var days = Enumerable.Range(0, 25).Select(i => new DateOnly(2026, 9, 1).AddDays(i)).ToArray();
        var shares = days.Select((d, i) => new SharePoint(d, 100 + i, null, "r" + i)).ToArray();
        var evidence = shares.Select(x => new EvidenceRow(x.RevisionId, "SZSE", "159915", x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            x.Shares.ToString(CultureInfo.InvariantCulture), "Accepted", 1, Hash, Hash, "https://www.szse.cn/fixture.json", Cutoff.ToString("O"), Cutoff.ToString("O"), Cutoff.ToString("O"), null)).ToArray();
        var coverage = new CoverageReport("SZSE", "159915", days[0], days[^1], 25, 25, 0, 0, 0, 0, 100, true, true, "CompleteRegisteredCalendar",
            days.Select((d, i) => new CoverageDay(d, "Accepted", "r" + i)).ToArray());
        var nav = new NavResearchDataset("SZSE", "159915", "synthetic-v1", Cutoff, Hash,
            [new("https://www.efunds.com.cn/fixture", Hash, Cutoff, 100)],
            days.Select((d, i) => new NavResearchPoint(d, 2 + i / 100m, 99, Cutoff, [Hash])).ToArray());
        var metadata = new AlertFundMetadata("SZSE", "159915", "synthetic-fund", "synthetic-manager", "synthetic-index", days[0], days[^1],
            "https://www.szse.cn/fixture-etf.pdf", Hash, "synthetic-reviewer", true);
        return new(new("SZSE", "159915"), days[20], days[^1], Cutoff, nav, coverage, shares, evidence, metadata);
    }
    public static void Run()
    {
        var input = Fixture(); var report = EtfNavStudy.Run(input); var last = report.Windows.Where(x => x.Date == input.Through).ToArray();
        Check(last[0] is { DeltaShares: 1, EstimatedAmountYuan: 2.24m, State: "Complete" }
            && last[1] is { DeltaShares: 5, EstimatedAmountYuan: 11.10m }
            && last[2] is { DeltaShares: 20, EstimatedAmountYuan: 42.90m }, "分别相加每日变化乘以同日单位净值，拒绝用累计净值或期末净值乘总变化");
        Check(report is { State: "CompleteStudyWindows", KnowledgeMode: "RetrospectiveOnly", TimingProof: "NotProven", InstitutionTradeState: "NotObserved" }, "研究金额不会升级为具名机构交易或已证实择时");
        var missingNav = EtfNavStudy.Run(input with { Nav = input.Nav with { Points = input.Nav.Points.Where(x => x.Date != input.Through).ToArray() } });
        Check(missingNav.Windows.Last() is { DeltaShares: 20, EstimatedAmountYuan: null, State: "NAV Unavailable" }, "缺当日NAV保留份额差额，金额未知不补零");
        var missingShares = EtfNavStudy.Run(input with { Shares = input.Shares.Where(x => x.Date != input.Through.AddDays(-1)).ToArray() });
        Check(missingShares.Windows.TakeLast(3).All(x => x.DeltaShares is null && x.EstimatedAmountYuan is null), "缺相邻份额不跨越缺日计算");
        Check(EtfNavStudy.Run(input with { Comparability = null }).Windows.All(x => x.State == "UnverifiedComparability" && x.EstimatedAmountYuan is null), "未知可比性阻断差额及金额");
        Check(EtfNavStudy.Run(input with { Comparability = input.Comparability! with { ComparableThrough = input.From.AddDays(-1) } }).Windows.All(x => x.EstimatedAmountYuan is null), "审核区间不足不延伸覆盖");
        Check(EtfNavStudy.Run(input with { Coverage = input.Coverage with { CalendarVerified = false } }).Windows.All(x => x.State == "UnverifiedCalendar"), "未核验日历拒绝猜测交易日");
        Check(EtfNavStudy.Run(input with { Coverage = input.Coverage with { CalendarRangeCovered = false } }).Windows.All(x => x.State == "UnverifiedCalendar"), "日历边界不足时不能只用已有日期");
        var pending = input.Coverage.Days.Select(x => x.Date == input.Through ? x with { State = "NeedsReview" } : x).ToArray();
        Check(EtfNavStudy.Run(input with { Coverage = input.Coverage with { Days = pending } }).Windows.TakeLast(3).All(x => x.State == "ShareEvidenceUnavailable"), "正式待审状态优先于旧份额点，不以旧接受值绕过冲突");
        var futureEvidence = input.ShareEvidence.Select(x => x.TradeDate == input.Through.ToString("yyyy-MM-dd") ? x with { AcceptedAt = Cutoff.AddTicks(1).ToString("O") } : x).ToArray();
        Check(EtfNavStudy.Run(input with { ShareEvidence = futureEvidence }).Windows.TakeLast(3).All(x => x.EstimatedAmountYuan is null), "知识截止后的份额审核不能用于研究");
        var mismatch = input.ShareEvidence.Select(x => x.TradeDate == input.Through.ToString("yyyy-MM-dd") ? x with { Shares = "999" } : x).ToArray();
        Check(EtfNavStudy.Run(input with { ShareEvidence = mismatch }).Windows.TakeLast(3).All(x => x.EstimatedAmountYuan is null), "修订ID相同但数量不符也不可用");
        var unknownTime = input.ShareEvidence.Select(x => x with { AcceptedAt = null }).ToArray();
        Check(EtfNavStudy.Run(input with { ShareEvidence = unknownTime }).Windows.All(x => x.EstimatedAmountYuan is null), "旧版未知审核时间保持不可计算");
        var reversedTime = input.ShareEvidence.Select(x => x with { AcceptedAt = Cutoff.AddSeconds(-1).ToString("O") }).ToArray();
        Check(EtfNavStudy.Run(input with { ShareEvidence = reversedTime }).Windows.All(x => x.EstimatedAmountYuan is null), "审核早于首见的矛盾时间链拒绝计算");
        Check(EtfNavStudy.Run(input with { Coverage = input.Coverage with { Days = [], RegisteredTradingDays = 0 } }).State == "NoRegisteredTradingDays", "无日历行显示未知状态而不是零金额成功");
        Check(EtfNavStudy.Run(input with { From = input.Coverage.From }).Windows.First().State == "IncompleteWarmup", "预热不足独立报告");
        Check(EtfNavStudy.Run(input with { Shares = input.Shares.Reverse().ToArray(), ShareEvidence = input.ShareEvidence.Reverse().ToArray() }).Fingerprint == report.Fingerprint, "排序变化不改变冻结研究身份");
        Check(EtfNavStudy.Run(input with { Nav = input.Nav with { Fingerprint = new('b', 64) } }).Fingerprint != report.Fingerprint, "相同NAV数值但来源身份变化必须改变金额研究指纹");
        Reject(() => EtfNavStudy.Run(input with { Key = new("SZSE", "159919") }), "跨基金NAV拒绝");
        Reject(() => EtfNavStudy.Run(input with { Shares = [..input.Shares, input.Shares[0]] }), "重复份额日期拒绝");
        Reject(() => EtfNavStudy.Run(input with { Shares = input.Shares.Select(x => x with { Nav = 9m }).ToArray() }), "调用方夹带未经本链核验的NAV拒绝");
        Reject(() => EtfNavStudy.Run(input with { KnowledgeCutoff = Cutoff.AddTicks(-1) }), "未来NAV验证快照拒绝");
        Reject(() => EtfNavStudy.Run(input with { Through = new DateOnly(2026, 10, 25) }), "未来研究日期拒绝");
        Reject(() => EtfNavStudy.Run(input with { Nav = input.Nav with { Points = input.Nav.Points.Select(x => x with { SourceHashes = [new('c', 64)] }).ToArray() } }), "NAV行无对应原件身份拒绝");
        ((SharePoint[])input.Shares)[0] = input.Shares[0] with { Shares = 99999 };
        Check(report.FrozenInputs.Shares[0].Shares == 100 && report.Fingerprint == EtfNavStudy.Run(report.FrozenInputs).Fingerprint, "深复制研究输入，可脱离调用方复算同一指纹");
    }
}
