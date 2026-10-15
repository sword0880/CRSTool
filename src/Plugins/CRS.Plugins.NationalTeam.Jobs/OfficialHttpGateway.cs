using System.IO;
using System.Net.Http;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeam.Jobs;
/// <summary>公开来源限域、限速和大小保护；不跟随重定向，不绕过403或429。</summary>
public sealed class OfficialHttpGateway : IPluginHttpGateway, IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient directClient;
    private readonly SourceRequestPolicy policy;
    private readonly HashSet<string> directHosts=new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1);
    public Func<EtfProgress,Task>? Progress {get;set;}
    private Task ReportAsync(string stage,Uri uri,int? status=null,DateTimeOffset? until=null)=>Progress?.Invoke(new(stage,Market:uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)?"SSE":"SZSE",HttpStatus:status,Until:until))??Task.CompletedTask;
    /// <summary>测试可注入固定响应；正式入口只使用禁重定向的默认传输。</summary>
    public OfficialHttpGateway(SourceRequestPolicy policy, HttpMessageHandler? transport = null)
    {
        this.policy = policy;
        client = new(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        directClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Host is not ("www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "www.szse.cn")) throw new InvalidOperationException("Public source not allowed.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = policy.Read(uri.Host);
            if (state.Blocked > DateTimeOffset.UtcNow) {await ReportAsync("Cooldown",uri,until:state.Blocked);throw new InvalidOperationException($"来源冷却中，最早重试时间{state.Blocked:O}。");}
            var wait = state.Next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) {await ReportAsync("RateWait",uri,until:state.Next);await Task.Delay(wait, cancellationToken).ConfigureAwait(false);}
            cancellationToken.ThrowIfCancellationRequested();
            policy.Reserve(uri.Host, DateTimeOffset.UtcNow);
            await ReportAsync("RequestStarted",uri);
            // 头部和正文共用20秒期限；HttpClient的ResponseHeadersRead本身不约束正文读取。
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var response = await SendAsync(uri,deadline.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                await ReportAsync("ResponseReceived",uri,status);
                if (status is 403 or 429 or >= 500)
                {
                    var retry = response.Headers.RetryAfter;
                    var until = retry?.Date;
                    if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
                    {
                        var now = DateTimeOffset.UtcNow;
                        until = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delta);
                    }
                    policy.Failure(uri.Host, DateTimeOffset.UtcNow, status, until);
                    await ReportAsync("Cooldown",uri,status,policy.Read(uri.Host).Blocked);
                }
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Response too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Response too large.");
                    await buffer.WriteAsync(bytes.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
                if (status is >= 200 and < 300) policy.Success(uri.Host);
                return new(status, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                policy.Failure(uri.Host, DateTimeOffset.UtcNow, 0);
                await ReportAsync("RequestTimedOut",uri,until:policy.Read(uri.Host).Blocked);
                throw new TimeoutException("官方来源响应超时，已进入持久退避。");
            }
            catch (HttpRequestException)
            {
                policy.Failure(uri.Host, DateTimeOffset.UtcNow, 0);
                await ReportAsync("RequestFailed",uri,until:policy.Read(uri.Host).Blocked);
                throw new HttpRequestException("官方来源连接失败，已进入持久退避。");
            }
        }
        finally { gate.Release(); }
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken token)
    {
        async Task<HttpResponseMessage> Send(HttpClient transport)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            // 官方公开页面对应的Referer，不携带Cookie、账号或验证码。
            var activity=uri.Query.Contains("COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",StringComparison.Ordinal) || uri.Query.Contains("scsj_gprdgk_after",StringComparison.Ordinal);
            request.Headers.Referrer=new Uri(uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)?activity?"https://www.sse.com.cn/market/stockdata/overview/day/":"https://www.sse.com.cn/market/funddata/volumn/etfvolumn/":activity?"https://www.szse.cn/market/stock/situation/daily/index.html":"https://fund.szse.cn/marketdata/etf/");
            return await transport.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        }
        if(directHosts.Contains(uri.Host))return await Send(directClient).ConfigureAwait(false);
        try{return await Send(client).ConfigureAwait(false);}
        catch(HttpRequestException error) when(error.StatusCode is null && !token.IsCancellationRequested)
        {
            // 系统代理握手/连接失败时只尝试同一公开URL直连；保留TLS证书验证。
            // HTTP403/429不走此分支，不换域名、验证码或身份，也不重复绕过来源拒绝。
            var response=await Send(directClient).ConfigureAwait(false);directHosts.Add(uri.Host);return response;
        }
    }
    public void Dispose() { client.Dispose(); directClient.Dispose(); policy.Dispose(); gate.Dispose(); }
}


