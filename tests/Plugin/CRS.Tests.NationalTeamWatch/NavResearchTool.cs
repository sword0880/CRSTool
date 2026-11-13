using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>仅读取已下载的公开原件，在新目录导出研究样本；不联网、不调用已安装服务、不写正式市场库。</summary>
internal static class NavResearchTool
{
    private sealed record DownloadReceipt(string Url, int Status, DateTimeOffset RetrievedAt, string Sha256, int Bytes);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EfundsNavResearch.ProductUrl] = "efunds-159915.html",
        [EfundsNavResearch.ArchiveUrl] = "efunds-159915-all.js",
        [EfundsNavResearch.RecentUrl] = "efunds-159915-1y.js",
        [EfundsNavResearch.DisplayUrl] = "efunds-product-detail-overview.js"
    };
    public static void Run(string input, string output)
    {
        var receipts = new List<DownloadReceipt>();
        receipts.Add(ReadReceipt(Path.Combine(input, "efunds-159915-receipt.json")));
        receipts.Add(ReadReceipt(Path.Combine(input, "efunds-1y-receipt.json")));
        receipts.AddRange(JsonSerializer.Deserialize<DownloadReceipt[]>(File.ReadAllText(Path.Combine(input, "efunds-assets-receipts.json"))) ?? throw new InvalidDataException("原件下载回执为空。"));
        if (receipts.Count != 4 || receipts.Select(x => x.Url).Distinct(StringComparer.Ordinal).Count() != 4
            || receipts.Any(x => !Files.ContainsKey(x.Url) || x.Status != 200 || x.Bytes is < 1 or > 2 * 1024 * 1024))
            throw new InvalidDataException("下载回执的来源、状态、大小或数量无效。");
        var documents = receipts.Select(receipt =>
        {
            var path = Path.Combine(input, Files[receipt.Url]);
            if (new FileInfo(path).Length != receipt.Bytes) throw new InvalidDataException("原件长度与回执不同。");
            return new CapturedNavDocument(receipt.Url, File.ReadAllBytes(path), receipt.RetrievedAt, receipt.Sha256);
        }).ToArray();
        var dataset = EfundsNavResearch.Build(documents, DateTimeOffset.UtcNow);
        var final = Path.GetFullPath(output);
        if (Directory.Exists(final) || File.Exists(final)) throw new IOException("输出目录已存在，拒绝覆盖研究证据。");
        var staging = final + ".preparing-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
        // 所有原件与报告就绪后才公开最终目录，失败的暂存目录不冒充完成报告。
        foreach (var document in documents) File.WriteAllBytes(Path.Combine(staging, Files[document.Url]), document.Content);
        File.WriteAllText(Path.Combine(staging, "dataset.json"), JsonSerializer.Serialize(dataset, Json), new UTF8Encoding(false));
        var csv = new StringBuilder("market,ticker,nav_date,unit_nav_yuan,accumulated_nav_yuan,first_seen_utc,published_at,knowledge_mode,calendar_state\n");
        foreach (var row in dataset.Points)
            csv.Append(CultureInfo.InvariantCulture, $"SZSE,159915,{row.Date:yyyy-MM-dd},{row.UnitNavYuan},{row.AccumulatedNavYuan},{row.FirstSeenAt.ToUniversalTime():O},,RetrospectiveOnly,NotVerified\n");
        File.WriteAllText(Path.Combine(staging, "nav-research.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(staging, "README.md"), $"""
            # 159915官方NAV隔离研究样本

            - 样本：{dataset.Points.Count}条，{dataset.Points[0].Date:yyyy-MM-dd}至{dataset.Points[^1].Date:yyyy-MM-dd}。
            - 解析版本：{dataset.ParserVersion}；指纹：{dataset.Fingerprint}。
            - 验证时间：{dataset.VerifiedAt.ToOffset(TimeSpan.FromHours(8)):O}；知识模式：RetrospectiveOnly。
            - 单位净值与累计净值分列。未填逐日官方发布时间；本次取得时间保留在每条记录与原件清单中。
            - 合并历史与最近一年时不允许重叠冲突；合并总数与页面总数一致，并逐条核对页面最近10日。
            - 官方历史可能包含周末期末估值，CalendarState=NotVerified；未证明逐交易日完整。
            - TimingProof=NotProven。不能把本次回填视为当年系统已知，不能据此宣称具名机构成交、资金流或高低位预测有效。
            - 此目录是离线来源验证结果，未写正式市场库，未启用自动采集或外发通知。

            原件与SHA256、取得时间见dataset.json的Sources；原件均在本目录。CSV便于本地核对，完整依据以JSON及原件为准。
            使用 --nav-research-verify <本目录> 按冻结验证时点重新解析原件，逐项比对完整JSON与指纹。
            """, new UTF8Encoding(false));
        Directory.Move(staging, final);
        Console.WriteLine(JsonSerializer.Serialize(new { Output = final, Count = dataset.Points.Count, From = dataset.Points[0].Date, Through = dataset.Points[^1].Date,
            dataset.Fingerprint, dataset.KnowledgeMode, dataset.CalendarState, dataset.TimingProof }, Json));
    }
    public static NavResearchDataset Verify(string directory)
    {
        var path = Path.Combine(directory, "dataset.json");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("研究数据集JSON超限。");
        var saved = File.ReadAllText(path);
        var frozen = JsonSerializer.Deserialize<NavResearchDataset>(saved) ?? throw new InvalidDataException("研究数据集为空。");
        if (frozen.Sources.Count != 4 || frozen.Sources.Any(s => !Files.ContainsKey(s.Url) || s.Bytes is < 1 or > 2 * 1024 * 1024))
            throw new InvalidDataException("研究原件清单无效。");
        var documents = frozen.Sources.Select(s =>
        {
            var raw = Path.Combine(directory, Files[s.Url]);
            if (new FileInfo(raw).Length != s.Bytes) throw new InvalidDataException("研究原件长度与冻结清单不同。");
            return new CapturedNavDocument(s.Url, File.ReadAllBytes(raw), s.RetrievedAt, s.Sha256);
        }).ToArray();
        var rebuilt = EfundsNavResearch.Build(documents, frozen.VerifiedAt);
        // 同时比较数值、身份、来源及只读的边界标记，不能仅信任JSON里保存的指纹。
        if (!JsonNode.DeepEquals(JsonNode.Parse(saved), JsonSerializer.SerializeToNode(rebuilt, Json)))
            throw new InvalidDataException("研究JSON与原件复算不同，拒绝篡改后的数值或知识模式。");
        Console.WriteLine($"PASS 冻结研究复算：{rebuilt.Points.Count}条，{rebuilt.Fingerprint}，{rebuilt.KnowledgeMode}/{rebuilt.TimingProof}");
        return rebuilt;
    }
    private static DownloadReceipt ReadReceipt(string path) => JsonSerializer.Deserialize<DownloadReceipt>(File.ReadAllText(path)) ?? throw new InvalidDataException("原件下载回执为空。");
    internal static IReadOnlyList<CapturedNavDocument> Documents(string directory)
    {
        var dataset = Verify(directory);
        return dataset.Sources.Select(s => new CapturedNavDocument(s.Url, File.ReadAllBytes(Path.Combine(directory, Files[s.Url])), s.RetrievedAt, s.Sha256)).ToArray();
    }
}
