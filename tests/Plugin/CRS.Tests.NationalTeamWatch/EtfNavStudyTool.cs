using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>只读正式份额与证据，使用单只ETF的已核验NAV研究快照；不写市场库或发送通知。</summary>
internal static class EtfNavStudyTool
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task Capture(string navDirectory, string fromText, string throughText, string output)
    {
        var nav = NavResearchTool.Verify(navDirectory);
        var from = DateOnly.ParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var through = DateOnly.ParseExact(throughText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (from > through || through.DayNumber - from.DayNumber > 3660) throw new InvalidDataException("研究区间无效。");
        var key = new EtfKey(nav.Market, nav.Ticker);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var token = deadline.Token;
        var pipe = new BackgroundPipeClient(); var workspace = new WorkerEtfWorkspace(pipe);
        async Task<BackgroundResponse> Health()
        {
            var response = await pipe.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "GetHealth"), token);
            if (response.Code != "Ok" || response.Health is not { State: "Running" }) throw new InvalidDataException("正式服务未返回完整Running状态。");
            return response;
        }
        var before = await Health();
        var coverage = await MonitorReadinessTool.Read(() => workspace.CoverageAsync(key, from.AddDays(-90), through, token), token);
        var points = await MonitorReadinessTool.Read(() => workspace.PointsAsync(key, token), token);
        var evidence = await MonitorReadinessTool.Read(() => workspace.EvidenceAsync(key, token), token);
        var metadata = await MonitorReadinessTool.Read(() => workspace.AlertMetadataAsync(token), token);
        var afterCoverage = await MonitorReadinessTool.Read(() => workspace.CoverageAsync(key, from.AddDays(-90), through, token), token);
        var after = await Health();
        if (before.Health!.ProcessId != after.Health!.ProcessId || JsonSerializer.Serialize(coverage) != JsonSerializer.Serialize(afterCoverage))
            throw new InvalidDataException("份额覆盖或服务进程在读取期间变化，拒绝拼接不一致的研究快照。");
        var selected = points.Where(x => x.Date >= coverage.From && x.Date <= through).ToArray();
        var ids = selected.Select(x => x.RevisionId).ToHashSet(StringComparer.Ordinal);
        var input = new EtfNavStudyRequest(key, from, through, DateTimeOffset.UtcNow, nav, coverage, selected,
            evidence.Where(x => ids.Contains(x.RevisionId)).ToArray(), metadata.SingleOrDefault(x => x.Market == key.Market && x.Ticker == key.Ticker));
        var report = EtfNavStudy.Run(input);
        var final = Path.GetFullPath(output);
        if (Directory.Exists(final) || File.Exists(final)) throw new IOException("研究输出已存在，拒绝覆盖。");
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        var frozenNav = Path.Combine(staging, "nav-snapshot"); Directory.CreateDirectory(frozenNav);
        // 只复制规定文件，复核后才允许发布报告；不接受调用方目录中的任意附加脚本。
        foreach (var name in new[] { "dataset.json", "efunds-159915.html", "efunds-159915-all.js", "efunds-159915-1y.js", "efunds-product-detail-overview.js" })
            File.Copy(Path.Combine(navDirectory, name), Path.Combine(frozenNav, name));
        if (NavResearchTool.Verify(frozenNav).Fingerprint != nav.Fingerprint) throw new InvalidDataException("复制期间NAV快照发生变化。");
        await File.WriteAllTextAsync(Path.Combine(staging, "report.json"), JsonSerializer.Serialize(report, Json), token);
        await File.WriteAllTextAsync(Path.Combine(staging, "worker-read.json"), JsonSerializer.Serialize(new { CheckedAt = input.KnowledgeCutoff, Before = before.Health, After = after.Health }, Json), token);
        var markdown = new StringBuilder($"# 159915同日份额/NAV隔离研究\n\n状态：{report.State}；{report.KnowledgeMode}；{report.TimingProof}；机构成交：{report.InstitutionTradeState}。\n\n{report.Basis}\n\n区间：{from:yyyy-MM-dd}至{through:yyyy-MM-dd}。知识截止：{input.KnowledgeCutoff.ToOffset(TimeSpan.FromHours(8)):O}。指纹：{report.Fingerprint}。\n\n");
        markdown.Append("份额和日历在本次正式Worker查询时经过其原件健康检查；份额原件继续保存在正式原件库，本目录冻结数值与证据身份，不代表独立移机原件验收。NAV四份原件随报告保存。\n\n| 窗口状态 | 数量 |\n| --- | --- |\n");
        foreach (var group in report.Windows.GroupBy(x => x.State).OrderBy(x => x.Key)) markdown.AppendLine($"| {group.Key} | {group.Count()} |");
        markdown.Append("\n可比性/日历/份额证据不足的窗口没有差额或金额；缺NAV时只保留可比份额差额。报告不补零、不代替量价/估值/广度、机构组合方向或样本外效果验证。\n\n使用 --nav-share-study-verify <本目录> 重新核验NAV原件并复算整个报告。\n");
        await File.WriteAllTextAsync(Path.Combine(staging, "report.md"), markdown.ToString(), new UTF8Encoding(false), token);
        token.ThrowIfCancellationRequested(); Directory.Move(staging, final);
        Console.WriteLine(JsonSerializer.Serialize(new { Output = final, report.State, Windows = report.Windows.Count, Complete = report.Windows.Count(x => x.EstimatedAmountYuan.HasValue),
            States = report.Windows.GroupBy(x => x.State).ToDictionary(x => x.Key, x => x.Count()), report.Fingerprint, report.TimingProof, report.InstitutionTradeState }, Json));
    }
    public static void Verify(string directory)
    {
        var path = Path.Combine(directory, "report.json");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("份额/NAV研究报告超限。");
        var saved = File.ReadAllText(path);
        var frozen = JsonSerializer.Deserialize<EtfNavStudyReport>(saved) ?? throw new InvalidDataException("研究报告为空。");
        var nav = NavResearchTool.Verify(Path.Combine(directory, "nav-snapshot"));
        var rebuilt = EtfNavStudy.Run(frozen.FrozenInputs with { Nav = nav });
        if (!JsonNode.DeepEquals(JsonNode.Parse(saved), JsonSerializer.SerializeToNode(rebuilt, Json)))
            throw new InvalidDataException("冻结报告与原件/输入复算不一致。");
        Console.WriteLine($"PASS 份额/NAV冻结复算：{rebuilt.Windows.Count}个窗口，{rebuilt.Fingerprint}，{rebuilt.State}/{rebuilt.TimingProof}");
    }
}
