using System.Net;
using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>固定来源、持久冷却和真实插件入口的隔离反例；所有HTTP均为替身，不访问源站。</summary>
internal static class NavSourceChecks
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS NAV采集：" + name); }
    public static async Task Run(string root)
    {
        await Gateway(Path.Combine(root, "gateway"));
        var http = new Fixture(); var context = new Context(Path.Combine(root, "session"));
        await using var session = new NationalTeamBackgroundSession(context, http); await session.StartAsync(default);
        Check(session.DescribeJobs().Where(x => x.JobId is "nav-sync" or "nav-review").All(x => !x.EnabledByDefault && !x.RunOnStartup && x.ManualOnly), "新任务仅手动，不在服务启动时自动联网");
        string Request(string operation, string ticker = "159915", string? revision = null, bool accept = false) => JsonSerializer.Serialize(new EtfRequest(operation, "SZSE", ticker, Revision: revision, Accept: accept, Reviewer: operation == "nav-review" ? "fixture-reviewer" : null));
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync", "159919"), default)).Status == "Rejected" && http.Calls == 0, "其他基金代码在联网前拒绝");
        var id = Guid.NewGuid();
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync"), default, id)).Status == "Succeeded" && http.Calls == 4, "固定四原件全部验证后写入真实插件仓储");
        http.FailOn = http.Calls + 1;
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync"), default, id)).Status == "Succeeded" && http.Calls == 4, "提交后执行回执丢失可重放，先读原回执而不重新下载"); http.FailOn = 0;
        var points = await Query<NavAcceptedPoint>(session, "nav-points");
        Check(points.Count == 10 && points[^1].UnitNavYuan == 1.09m, "NAV专用读取保留单位净值和独立修订ID");
        Check(session.QueryOperation(JsonSerializer.Serialize(new EtfRequest("nav-revisions"))) == "nav-revisions", "新查询操作进入诊断白名单");
        http.Documents = NavStorageChecks.Corrected();
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync"), default, Guid.NewGuid())).Status == "NeedsReview", "源站更正明确报告待审，不把写入候选叫作全成功");
        Check((await Query<NavAcceptedPoint>(session, "nav-points")).Count == 9, "正式查询遇到冲突阻断该日NAV");
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync"), default, Guid.NewGuid())).Status == "NeedsReview", "重复收到待审值仍报告未解决冲突");
        var revisions = await Query<NavRevision>(session, "nav-revisions"); var pending = revisions.Single(x => x.State == "NeedsReview");
        Check((await session.ExecuteAsync("nav-review", Request("nav-review", "159919", pending.Id, true), default)).Status == "Rejected", "审核不接受与修订不符的证券参数");
        Check((await session.ExecuteAsync("nav-review", Request("nav-review", revision: pending.Id, accept: true), default, Guid.NewGuid())).Status == "Succeeded"
            && (await Query<NavAcceptedPoint>(session, "nav-points"))[^1].UnitNavYuan == 1.0901m, "审核通过后按独立NAV身份回读更正值");
        http.FailOn = http.Calls + 2;
        Check((await session.ExecuteAsync("nav-sync", Request("nav-sync"), default, Guid.NewGuid())).Status == "RetryDeferred", "第二原件失败不能用旧缓存冒充本次成功");
        Check((await Query<NavRevision>(session, "nav-revisions")).Count == revisions.Count, "部分联网失败不写入半份NAV事实");
        try { await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("nav-points", "SZSE", "159915", new(2020, 1, 1), new(2026, 10, 10))), default); throw new Exception("超限未拒绝"); }
        catch (InvalidDataException) { Check(true, "过长日期范围拒绝，不返回截断历史"); }
        var bad = new Fixture { Documents = NavResearchChecks.Fixture().Select(x => x.Url == EfundsNavResearch.DisplayUrl
            ? x with { Content = Encoding.UTF8.GetBytes("bad mapping") } : x).ToArray() };
        try { await new OfficialNavSource(bad).FetchAsync(default); throw new Exception("坏字段未拒绝"); }
        catch (InvalidDataException) { Check(bad.Rejections == 1, "字段契约破坏通知同组来源策略冷却"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var cancelled = new Fixture();
        try { await new OfficialNavSource(cancelled).FetchAsync(cancel.Token); throw new Exception("忽略取消"); }
        catch (OperationCanceledException) { Check(cancelled.Calls == 0, "采集取消不继续发起新请求"); }
    }
    private static async Task<List<T>> Query<T>(NationalTeamBackgroundSession session, string operation)
    {
        var request = new EtfRequest(operation, "SZSE", "159915", new(2026, 10, 1), new(2026, 10, 10)); var result = new List<T>();
        do
        {
            var page = JsonSerializer.Deserialize<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request), default))!;
            result.AddRange(JsonSerializer.Deserialize<List<T>>(page.Items)!);
            if (page.NextOffset is not { } next) return result;
            request = request with { Snapshot = page.Snapshot, Offset = next };
        } while (true);
    }
    private static async Task Gateway(string root)
    {
        var handler = new DeniedHandler(); var policy = new SourceRequestPolicy(root, TestMarketData.Key);
        using (var gateway = new OfficialHttpGateway(policy, handler))
        {
            foreach (var url in new[] { "https://www.efunds.com.cn/fund/159919.shtml", EfundsNavResearch.RecentUrl + "?other=1", "https://evil.efunds.com.cn/fund/159915.shtml" })
            {
                try { await gateway.GetAsync(new(url), default); throw new Exception("未获准URL被放行"); }
                catch (InvalidOperationException) { Check(handler.Calls == 0, "固定路径之外的基金URL在HTTP前拒绝"); }
            }
            Check((await gateway.GetAsync(new(EfundsNavResearch.ProductUrl), default)).StatusCode == 403, "来源拒绝保留真实HTTP状态");
            try { await gateway.GetAsync(new(EfundsNavResearch.RecentUrl), default); throw new Exception("CDN绕过拒绝"); }
            catch (InvalidOperationException) { Check(handler.Calls == 1, "官网403后CDN共享冷却而不发请求"); }
            Check(policy.Read("www.efunds.com.cn").Blocked == policy.Read("cdn.efunds.com.cn").Blocked && policy.Read("www.sse.com.cn").Blocked <= DateTimeOffset.UtcNow,
                "基金双域同组，交易所预算保持独立");
        }
        using var reopened = new SourceRequestPolicy(root, TestMarketData.Key);
        Check(reopened.Read("cdn.efunds.com.cn").Blocked > DateTimeOffset.UtcNow, "重启保留基金来源拒绝状态");
    }
    private sealed class Context(string root) : IBackgroundHostContext
    {
        public string PluginId => "crs.national-team"; public string DataDirectory => root; public byte[] OpenDataKey() => TestMarketData.Key.ToArray();
    }
    private sealed class Fixture : IPluginHttpGateway, IOfficialSourceValidationFeedback
    {
        public CapturedNavDocument[] Documents = NavResearchChecks.Fixture(); public int Calls, FailOn, Rejections;
        public Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(Calls == FailOn ? new PublicHttpResponse(503, [], "text/plain") : new(200, Documents.Single(x => x.Url == uri.AbsoluteUri).Content, "application/javascript"));
        }
        public void RejectContract(Uri uri) => Rejections++;
    }
    private sealed class DeniedHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent("denied"u8.ToArray()) }); }
    }
}
