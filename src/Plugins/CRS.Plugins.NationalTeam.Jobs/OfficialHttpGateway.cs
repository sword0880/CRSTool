using System.IO;
using System.Net.Http;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeam.Jobs;
/// <summary>公开来源限域、限速和大小保护；不跟随重定向，不绕过403或429。</summary>
public sealed class OfficialHttpGateway : IPluginHttpGateway, IOfficialSourceValidationFeedback, IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient directClient;
    private readonly ISourceRequestPolicy policy;
    private readonly IBackgroundSourceContext? sources;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> directHosts=new(StringComparer.OrdinalIgnoreCase);
    private readonly object policyGate=new();
    private readonly Dictionary<string,SemaphoreSlim> sourceGates=new(StringComparer.Ordinal)
        {{"SSE",new(1)},{"SZSE",new(1)},{"ISSUER-600153",new(1)}};
    private static string Source(string host)=>host.EndsWith("sse.com.cn",StringComparison.Ordinal)?"SSE":host.EndsWith("szse.cn",StringComparison.Ordinal)?"SZSE":"ISSUER-600153";
    private (DateTimeOffset Next,DateTimeOffset Blocked,int Failures) ReadPolicy(string host){lock(policyGate)return policy.Read(host);}
    public DateTimeOffset? RetryAt(Uri uri)
    {
        var blocked=ReadPolicy(uri.Host).Blocked;
        return blocked>CRS.Timing.BeijingClock.Now?blocked:null;
    }
    public void RejectContract(Uri uri)
    {
        sources?.ReportSourceFailure(uri.Host,0);
        lock(policyGate)policy.Failure(uri.Host,CRS.Timing.BeijingClock.Now,0);
        ImportPolicy(uri.Host);
    }
    private void ImportPolicy(string host)
    {
        if(sources is null)return;
        var state=ReadPolicy(host);
        sources.ImportSourceState(host,new(state.Next,state.Blocked,state.Failures));
    }
    private void Failure(Uri uri,int status,IBackgroundSourceLease? lease,DateTimeOffset? retryAfter=null)
    {
        // 宿主先登记；插件库写失败也不能让其他插件继续访问已拒绝来源。
        lease?.Complete(status,retryAfter);
        lock(policyGate)policy.Failure(uri.Host,CRS.Timing.BeijingClock.Now,status,retryAfter);
        ImportPolicy(uri.Host);
    }
    public Func<EtfProgress,Task>? Progress {get;set;}
    private Task ReportAsync(string stage,Uri uri,int? status=null,DateTimeOffset? until=null)=>Progress?.Invoke(new(stage,Market:uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)||OfficialEquityDocuments.IsReviewedIssuer(uri)?"SSE":"SZSE",HttpStatus:status,Until:until))??Task.CompletedTask;
    /// <summary>测试可注入固定响应；正式入口只使用禁重定向的默认传输。</summary>
    public OfficialHttpGateway(ISourceRequestPolicy policy, HttpMessageHandler? transport = null,IBackgroundSourceContext? sources=null)
    {
        this.policy = policy;
        this.sources = sources;
        // 会话Start在调度启动前合并所有旧来源，防止其他插件先请求空宿主预算。
        if(sources is not null)
        {
            try
            {
                foreach(var host in new[]{"www.sse.com.cn","www.szse.cn","www.chinacnd.com"})
                {
                    var legacy=policy.Read(host);
                    sources.ImportSourceState(host,new(legacy.Next,legacy.Blocked,legacy.Failures));
                }
            }
            catch{policy.Dispose();throw;}
        }
        client = new(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        directClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<PublicHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Host is not ("www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" or "www.szse.cn" or "disc.static.szse.cn")&&!OfficialEquityDocuments.IsReviewedIssuer(uri)) throw new InvalidOperationException("Public source not allowed.");
        var gate=sourceGates[Source(uri.Host)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadPolicy(uri.Host);
            using var lease = await AcquireSourceAsync(uri,state,cancellationToken).ConfigureAwait(false);
            if (state.Blocked > CRS.Timing.BeijingClock.Now) {await ReportAsync("Cooldown",uri,until:state.Blocked);throw new InvalidOperationException($"来源冷却中，最早重试时间{state.Blocked:O}。");}
            var wait = state.Next - CRS.Timing.BeijingClock.Now;
            if (wait > TimeSpan.Zero) {await ReportAsync("RateWait",uri,until:state.Next);await Task.Delay(wait, cancellationToken).ConfigureAwait(false);}
            cancellationToken.ThrowIfCancellationRequested();
            lock(policyGate)policy.Reserve(uri.Host, CRS.Timing.BeijingClock.Now);
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
                        var now = CRS.Timing.BeijingClock.Now;
                        until = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delta);
                    }
                    Failure(uri,status,lease,until);
                    await ReportAsync("Cooldown",uri,status,ReadPolicy(uri.Host).Blocked);
                }
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Response too large.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Response too large.");
                    await buffer.WriteAsync(bytes.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
                if (status is >= 200 and < 300)
                {
                    lock(policyGate)if(policy.Read(uri.Host).Blocked<=CRS.Timing.BeijingClock.Now)policy.Success(uri.Host);
                    lease?.Complete(status);
                }
                return new(status, buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Failure(uri,0,lease);
                await ReportAsync("RequestTimedOut",uri,until:ReadPolicy(uri.Host).Blocked);
                var failure=new TimeoutException("官方来源响应超时，已进入持久退避。");
                failure.Data["SourceRetryAt"]=ReadPolicy(uri.Host).Blocked;throw failure;
            }
            catch (HttpRequestException)
            {
                Failure(uri,0,lease);
                await ReportAsync("RequestFailed",uri,until:ReadPolicy(uri.Host).Blocked);
                var failure=new HttpRequestException("官方来源连接失败，已进入持久退避。");
                failure.Data["SourceRetryAt"]=ReadPolicy(uri.Host).Blocked;throw failure;
            }
        }
        finally { gate.Release(); }
    }
    private async Task<IBackgroundSourceLease?> AcquireSourceAsync(Uri uri,(DateTimeOffset Next,DateTimeOffset Blocked,int Failures) state,CancellationToken token)
    {
        if(sources is null)return null;
        await ReportAsync("RateWait",uri);
        try{return await sources.AcquireSourceAsync(uri.Host,new(state.Next,state.Blocked,state.Failures),token).ConfigureAwait(false);}
        catch(SourceBudgetUnavailableException error)
        {
            await ReportAsync(error.Code=="SourceCooldown"?"Cooldown":"RateWait",uri,until:error.Until);
            throw;
        }
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken token)
    {
        async Task<HttpResponseMessage> Send(HttpClient transport)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            // 官方公开页面对应的Referer，不携带Cookie、账号或验证码。
            var activity=uri.Query.Contains("COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",StringComparison.Ordinal) || uri.Query.Contains("scsj_gprdgk_after",StringComparison.Ordinal);
            request.Headers.Referrer=new Uri(uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)?activity?"https://www.sse.com.cn/market/stockdata/overview/day/":"https://www.sse.com.cn/market/funddata/volumn/etfvolumn/":activity?"https://www.szse.cn/market/stock/situation/daily/index.html":"https://fund.szse.cn/marketdata/etf/");
            if(uri.Query.Contains("COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",StringComparison.Ordinal))request.Headers.Referrer=new("https://www.sse.com.cn/assortment/stock/list/info/company/index.shtml");
            if(uri.AbsolutePath=="/security/stock/queryCompanyBulletinNew.do")request.Headers.Referrer=new("https://www.sse.com.cn/assortment/stock/list/info/company/index.shtml");
            if(OfficialEquityDocuments.IsReviewedIssuer(uri))request.Headers.Referrer=new("https://www.chinacnd.com/report.html");
            return await transport.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        }
        if(directHosts.ContainsKey(uri.Host))return await Send(directClient).ConfigureAwait(false);
        try{return await Send(client).ConfigureAwait(false);}
        catch(HttpRequestException error) when(error.StatusCode is null && !token.IsCancellationRequested)
        {
            // 系统代理握手/连接失败时只尝试同一公开URL直连；保留TLS证书验证。
            // HTTP403/429不走此分支，不换域名、验证码或身份，也不重复绕过来源拒绝。
            var response=await Send(directClient).ConfigureAwait(false);directHosts.TryAdd(uri.Host,0);return response;
        }
    }
    public void Dispose() { client.Dispose(); directClient.Dispose();lock(policyGate)policy.Dispose();foreach(var gate in sourceGates.Values)gate.Dispose(); }
}


