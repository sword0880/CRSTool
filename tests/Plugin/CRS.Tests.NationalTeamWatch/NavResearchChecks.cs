using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Application;

internal static class NavResearchChecks
{
    private static readonly DateTimeOffset Retrieved = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Verified = Retrieved.AddMinutes(1);
    private static readonly string[] Names = ["Date", "AccIncomeRatio", "NetValue", "AccNetValue", "Change", "IncomeRatio", "IncomeUnit", "HundredIncomeUnit", "TrackIndex"];
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS 净值研究：" + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS 净值研究：" + name); return; }
        throw new Exception("未拒绝：" + name);
    }
    private static CapturedNavDocument Capture(string url, string content) => Capture(url, Encoding.UTF8.GetBytes(content));
    private static CapturedNavDocument Capture(string url, byte[] content) => new(url, content, Retrieved, Convert.ToHexString(SHA256.HashData(content)));
    private static string Row(int i) => $"202610{(i + 1):00}_0.10000000_{(1m + i / 100m).ToString("0.0000", CultureInfo.InvariantCulture)}_2.0000_0.00";
    internal static CapturedNavDocument[] Fixture()
    {
        var page = new StringBuilder("var fundCode = \"159915\"; var maincode = '159915';");
        foreach (var url in new[] { EfundsNavResearch.ArchiveUrl, EfundsNavResearch.RecentUrl, EfundsNavResearch.DisplayUrl }) page.Append($"<script src=\"{url}\"></script>");
        page.Append("<thead id=\"navTHead\"><tr><th>日期</th><th>单位净值(元)</th><th>日涨跌</th><th>累计净值(元)</th><th>资产净值(元)</th></tr></thead><tbody id=\"navTBody\">");
        foreach (var i in Enumerable.Range(0, 10).Reverse()) page.Append(CultureInfo.InvariantCulture, $"<tr data-total=\"10\"><td>2026-10-{i + 1:00}</td><td>{1m + i / 100m:0.0000}</td><td>0.00%</td><td>2.0000</td><td>--</td></tr>");
        page.Append("</tbody>");
        return [Capture(EfundsNavResearch.ProductUrl, page.ToString()),
            Capture(EfundsNavResearch.ArchiveUrl, "mk_159915_all=\"0_1_2_3_4;" + string.Join(';', Enumerable.Range(0, 7).Select(Row)) + ";;\";"),
            Capture(EfundsNavResearch.RecentUrl, "mk_159915_1y=\"0_1_2_3_4;" + string.Join(';', Enumerable.Range(5, 5).Select(Row)) + ";\";"),
            Capture(EfundsNavResearch.DisplayUrl, "var yNames = [" + string.Join(',', Names.Select(x => "\"EnumSourceType" + x + "\"")) + "]; ")];
    }
    private static CapturedNavDocument[] Change(int index, Func<string, string> mutation)
    {
        var docs = Fixture(); docs[index] = Capture(docs[index].Url, mutation(Encoding.UTF8.GetString(docs[index].Content))); return docs;
    }
    public static void Run()
    {
        var result = EfundsNavResearch.Build(Fixture(), Verified);
        Check(result.Points.Count == 10 && result.Points[0].UnitNavYuan == 1m && result.Points[^1].UnitNavYuan == 1.09m
            && result.Points[^1].AccumulatedNavYuan == 2m, "去重合并、按日排序，单位净值与累计净值独立");
        Check(result.Points[5].SourceHashes.Count == 2 && result.Sources.Count == 4, "重叠记录保留两份净值原件与字段/页面依据");
        Check(result is { KnowledgeMode: "RetrospectiveOnly", TimingProof: "NotProven", CalendarState: "NotVerified" }
            && result.Points.All(x => x.PublishedAt is null && x.FirstSeenAt == Retrieved), "历史日期不倒填公告或系统首见时间，也不生成交易日历");
        Check(result.Fingerprint == EfundsNavResearch.Build(Fixture().Reverse().ToArray(), Verified).Fingerprint, "冻结原件次序不影响复算指纹");
        Check(result.Fingerprint != EfundsNavResearch.Build(Fixture(), Verified.AddSeconds(1)).Fingerprint, "不同验证时点不能沿用原知识身份");
        var later = Fixture().Select(x => x with { RetrievedAt = x.RetrievedAt.AddSeconds(1) }).ToArray();
        Check(result.Fingerprint != EfundsNavResearch.Build(later, Verified).Fingerprint, "数值相同的后来取得原件有不同知识指纹");
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Check(EfundsNavResearch.Build(Fixture(), Verified).Fingerprint == result.Fingerprint, "小数解析与指纹不受本机区域设置影响"); }
        finally { CultureInfo.CurrentCulture = culture; }
        Reject(() => EfundsNavResearch.Build(Change(0, x => x.Replace("159915", "159919")), Verified), "不同基金拒绝");
        Reject(() => EfundsNavResearch.Build(Change(0, x => x + "var fundCode = \"159919\";"), Verified), "含矛盾身份的页面拒绝");
        Reject(() => EfundsNavResearch.Build(Change(0, x => x.Replace("单位净值(元)", "单位净值(美元)")), Verified), "币种变化拒绝");
        Reject(() => EfundsNavResearch.Build(Change(3, x => x.Replace("EnumSourceTypeNetValue", "EnumSourceTypeIOPV")), Verified), "字段意义变化拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("0_1_2_3_4", "0_1_3_2_4")), Verified), "累计与单位列交换拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x + "alert('injected');"), Verified), "脚本尾部混入代码拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("mk_159915", "mk_159919")), Verified), "历史文件代码变化拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0500_", "_1.0501_")), Verified), "重叠历史冲突拒绝而不是覆盖");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0900_", "_1.0999_")), Verified), "最新页面交叉核对冲突拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace(Row(9) + ";", "")), Verified), "最近文件截断拒绝");
        Reject(() => EfundsNavResearch.Build(Change(0, x => x.Replace("data-total=\"10\"", "data-total=\"11\"")), Verified), "官方总数不符拒绝");
        Reject(() => EfundsNavResearch.Build(Change(0, x => x.Replace("</tbody>", "<tr>坏行</tr></tbody>")), Verified), "未识别页面行不能跳过");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("20261010", "20261025")), Verified), "原件取得日之后的净值拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("20261010", "20261009")), Verified), "同文件重复日期拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("20261010", "20261004")), Verified), "倒序历史拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0900_", "_0.0000_")), Verified), "零净值拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0900_", "_-1.0000_")), Verified), "负净值拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0900_", "_NaN_")), Verified), "非数值拒绝");
        Reject(() => EfundsNavResearch.Build(Change(2, x => x.Replace("_1.0900_", "_1e2_")), Verified), "未声明科学记数格式拒绝");
        var tampered = Fixture(); tampered[2].Content[0] = (byte)'X';
        Reject(() => EfundsNavResearch.Build(tampered, Verified), "原件哈希不符拒绝");
        var invalidUtf8 = Fixture(); invalidUtf8[2] = Capture(invalidUtf8[2].Url, [0xFF, 0xFE]);
        Reject(() => EfundsNavResearch.Build(invalidUtf8, Verified), "损坏编码拒绝");
        var oversized = Fixture(); oversized[2] = Capture(oversized[2].Url, new byte[2 * 1024 * 1024 + 1]);
        Reject(() => EfundsNavResearch.Build(oversized, Verified), "超限原件拒绝");
        Reject(() => EfundsNavResearch.Build(Fixture()[..3], Verified), "缺少字段说明原件拒绝");
        var badUrl = Fixture(); badUrl[1] = badUrl[1] with { Url = EfundsNavResearch.ArchiveUrl + "?alternate=1" };
        Reject(() => EfundsNavResearch.Build(badUrl, Verified), "非固定官方地址拒绝");
        Reject(() => EfundsNavResearch.Build(Fixture(), Retrieved.AddTicks(-1)), "验证时点之外的未来取得原件拒绝");
        var unknownTime = Fixture(); unknownTime[1] = unknownTime[1] with { RetrievedAt = default };
        Reject(() => EfundsNavResearch.Build(unknownTime, Verified), "未知取得时间拒绝");
        var original = Fixture(); var frozen = EfundsNavResearch.Build(original, Verified); original[1].Content[0] = (byte)'X';
        Check(frozen.Fingerprint == result.Fingerprint && frozen.Points[0].UnitNavYuan == 1m, "解析后修改调用方输入不改变冻结研究样本");
    }
}
