using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>固定网络响应验证持久退避，不访问真实官网、不等待真实冷却时间。</summary>
internal static class SourcePolicyChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.SourcePolicy", Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        int passed = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS 来源策略：" + name); }
        var handler = new FixedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(2));
            return Task.FromResult(response);
        });
        var sse = new Uri("https://query.sse.com.cn/test");
        using (var gateway = new OfficialHttpGateway(new(root, key), handler))
            Check((await gateway.GetAsync(sse, default)).StatusCode == 429, "拒绝响应保留原始状态供来源登记");
        using (var policy = new SourceRequestPolicy(root, key))
        {
            Check(policy.Read("www.sse.com.cn").Blocked > DateTimeOffset.UtcNow.AddHours(47), "重建策略后保留Retry-After，且同交易所域名共用阻断");
            Check(policy.Read("www.szse.cn").Failures == 0, "上交所拒绝不污染深交所额度");
        }
        var healthy = new FixedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));
        using (var gateway = new OfficialHttpGateway(new(root, key), healthy))
        {
            try { await gateway.GetAsync(new("https://etf.sse.com.cn/test"), default); throw new Exception("未阻断"); }
            catch (InvalidOperationException) { Check(healthy.Calls == 0, "重启后被阻断来源不会发出请求或切换域名"); }
            Check((await gateway.GetAsync(new("https://www.szse.cn/test"), default)).StatusCode == 200, "另一交易所仍可独立采集");
        }
        using (var policy = new SourceRequestPolicy(Path.Combine(root, "transient"), key))
        {
            var now = DateTimeOffset.UtcNow;
            policy.Reserve(sse.Host, now);
            Check(policy.Read(sse.Host).Next >= now.AddSeconds(3), "请求发出前先持久保留来源间隔");
            policy.Failure(sse.Host, now, 503);
            var first = policy.Read(sse.Host);
            policy.Failure(sse.Host, now, 503);
            Check(first.Blocked >= now.AddMinutes(5) && policy.Read(sse.Host).Blocked >= now.AddMinutes(10), "连续临时故障指数退避并带正向抖动");
            policy.Success(sse.Host);
            Check(policy.Read(sse.Host).Failures == 0, "成功后清除连续失败计数");
        }
        var timeoutRoot = Path.Combine(root, "timeout");
        using (var gateway = new OfficialHttpGateway(new(timeoutRoot, key), new FixedHandler((_, _) => throw new TaskCanceledException())))
        {
            try { await gateway.GetAsync(sse, default); throw new Exception("未分类超时"); }
            catch (TimeoutException) { Check(true, "传输超时转换为来源故障而非用户取消"); }
        }
        using (var policy = new SourceRequestPolicy(timeoutRoot, key))
            Check(policy.Read(sse.Host).Blocked > DateTimeOffset.UtcNow, "传输超时在重建后仍退避");
        using (var gateway = new OfficialHttpGateway(new(Path.Combine(root, "body"), key), new FixedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }))))
        {
            try { await gateway.GetAsync(sse, default); throw new Exception("正文未超时"); }
            catch (TimeoutException) { Check(true, "正文停滞也受20秒总期限约束"); }
        }
        var cancelRoot = Path.Combine(root, "cancel");
        using (var cancelled = new CancellationTokenSource())
        using (var gateway = new OfficialHttpGateway(new(cancelRoot, key), new FixedHandler((_, token) => { cancelled.Cancel(); token.ThrowIfCancellationRequested(); throw new Exception(); })))
        {
            try { await gateway.GetAsync(sse, cancelled.Token); throw new Exception("未取消"); }
            catch (OperationCanceledException) { Check(true, "调用方取消保持取消语义"); }
        }
        using (var policy = new SourceRequestPolicy(cancelRoot, key))
            Check(policy.Read(sse.Host).Failures == 0, "用户取消不增加来源失败计数");
        var rejected = false;
        try
        {
            using var plain = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "source-policy.db"), Pooling = false }.ToString());
            plain.Open(); using var command = plain.CreateCommand(); command.CommandText = "SELECT * FROM source_policy"; command.ExecuteScalar();
        }
        catch (SqliteException) { rejected = true; }
        Check(rejected, "来源策略库无钥拒读");
        var damaged = Path.Combine(root, "damaged"); Directory.CreateDirectory(damaged);
        File.WriteAllText(Path.Combine(damaged, "source-policy.db"), "broken database");
        try { using var policy = new SourceRequestPolicy(damaged, key); throw new Exception("损坏库被重建"); }
        catch (SqliteException) { Check(true, "策略损坏拒绝启动且不静默清除阻断"); }
        using (var policy = new SourceRequestPolicy(Path.Combine(root, "forbidden"), key))
        {
            var now = DateTimeOffset.UtcNow; policy.Failure(sse.Host, now, 403);
            Check(policy.Read(sse.Host).Blocked >= now.AddHours(6), "403至少冷却六小时");
        }

        // 复现旧超时异常：上交所TaskCanceledException不能阻止深交所返回已验证数据。
        var source = new OfficialShareSources(new TimeoutThenSzse(), new RawEvidenceStore(Path.Combine(root, "raw")));
        var result = await source.FetchAsync(new(2026, 9, 1), [new("SSE", "510300"), new("SZSE", "159919")], default);
        Check(result.Pages.Count == 1 && result.Issues?.Count == 1, "一市超时仍保留另一市事实与独立错误");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await source.FetchAsync(new(2026, 9, 1), [new("SSE", "510300")], cancelled.Token); throw new Exception("整批未取消"); }
            catch (OperationCanceledException) { Check(true, "整批取消仍停止来源采集"); }
        }
        CryptographicOperations.ZeroMemory(key);
        Console.WriteLine($"来源策略通过：{passed}项。现场：{root}");
    }

    private sealed class FixedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return response(request, cancellationToken); }
    }

    // 正文永不自行完成，必须由网关的总期限取消读取。
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    private sealed class TimeoutThenSzse : IPluginHttpGateway
    {
        public Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken token)
        {
            if (uri.Host.EndsWith("sse.com.cn", StringComparison.Ordinal)) throw new TaskCanceledException();
            const string json = """[{"metadata":{"catalogid":"fund_jjgm","cols":{"current_size":"基金规模<br>(万份)"},"recordcount":1,"pagecount":1,"pageno":1},"data":[{"size_date":"2026-09-01","fund_code":"159919","current_size":"684,831.66"}],"error":null}]""";
            return Task.FromResult(new PublicHttpResponse(200, Encoding.UTF8.GetBytes(json), "application/json"));
        }
    }
}
