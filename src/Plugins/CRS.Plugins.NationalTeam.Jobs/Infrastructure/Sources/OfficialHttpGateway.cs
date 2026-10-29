using System.IO;
using System.Net.Http;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
namespace CRS.Plugins.NationalTeam.Jobs;
/// <summary>公开来源限域、限速和大小保护；不跟随重定向，不绕过403或429。</summary>
public sealed class OfficialHttpGateway : IPluginHttpGateway, IPluginJsonHttpGateway, IOfficialSourceValidationFeedback, IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClient directClient;
    private readonly ISourceRequestPolicy policy;
    private readonly IBackgroundSourceContext? sources;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> directHosts=new(StringComparer.OrdinalIgnoreCase);
    private readonly object policyGate=new();
    private readonly Dictionary<string,SemaphoreSlim> sourceGates=new(StringComparer.Ordinal)
        {{"SSE",new(1)},{"SZSE",new(1)},{"ISSUER-600153",new(1)},{"ISSUER-601398",new(1)},{"ISSUER-601288",new(1)},{"ISSUER-603195",new(1)},{"EFUNDS",new(1)},{"CNINDEX",new(1)}};
    // 基金官网与其CDN共用一个预算；不能把未知域名默认归入已授权发行人。
    private static string Source(string host)=>host switch
    {
        "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn"=>"SSE",
        "www.szse.cn" or "disc.static.szse.cn"=>"SZSE",
        "www.chinacnd.com"=>"ISSUER-600153",
        "v.icbc.com.cn"=>"ISSUER-601398",
        // 已核农行原件单独排队和退避，不动SSE或其他发行人预算。
        "www.abchina.com"=>"ISSUER-601288",
        // 发行人网站与其明确披露CDN共用冷却；仅固定已核PDF能通过能力白名单，不借用SSE预算。
        "www.gongniu.cn" or "gnstaticcdn.gongniukaiguan.net"=>"ISSUER-603195",
        "www.efunds.com.cn" or "cdn.efunds.com.cn"=>"EFUNDS",
        "www.cnindex.com.cn" or "hq.cnindex.com.cn"=>"CNINDEX",
        _=>throw new InvalidOperationException("来源域名未登记。")
    };
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
    // 基金NAV及行业统计不属于两市份额；来源组由共享预算记录，不能借Market字段误标为深市。
    private Task ReportAsync(string stage,Uri uri,int? status=null,DateTimeOffset? until=null)=>Progress?.Invoke(new(stage,Market:EfundsNavResearch.IsSource(uri)||OfficialIndustryPeSource.IsSource(uri)||OfficialIndexSeriesSource.IsSource(uri)?null:uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)||OfficialEquityDocuments.IsReviewedIssuer(uri)?"SSE":"SZSE",HttpStatus:status,Until:until))??Task.CompletedTask;
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
                foreach(var host in new[]{"www.sse.com.cn","www.szse.cn","www.chinacnd.com","v.icbc.com.cn","www.abchina.com","gnstaticcdn.gongniukaiguan.net","www.efunds.com.cn","www.cnindex.com.cn"})
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
        =>await RequestAsync(uri,null,cancellationToken).ConfigureAwait(false);
    public async Task<PublicHttpResponse> PostJsonAsync(Uri uri,byte[] json,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(json.Length is 0 or >4096)throw new InvalidDataException("公开JSON查询请求超过限额。");
        var frozen=json.ToArray();OfficialEtfReportIndex.ValidatePost(uri,frozen);
        return await RequestAsync(uri,frozen,cancellationToken).ConfigureAwait(false);
    }
    private async Task<PublicHttpResponse> RequestAsync(Uri uri,byte[]? jsonBody,CancellationToken cancellationToken)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Host is not ("www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" or "www.szse.cn" or "disc.static.szse.cn")&&!OfficialEquityDocuments.IsReviewedIssuer(uri)&&!EfundsNavResearch.IsSource(uri)&&!OfficialIndustryPeSource.IsSource(uri)&&!OfficialIndexSeriesSource.IsSource(uri)) throw new InvalidOperationException("Public source not allowed.");
        var gate=sourceGates[Source(uri.Host)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadPolicy(uri.Host);
            using var lease = await AcquireSourceAsync(uri,state,cancellationToken).ConfigureAwait(false);
            if (state.Blocked > CRS.Timing.BeijingClock.Now) {await ReportAsync("Cooldown",uri,until:state.Blocked);throw new SourceBudgetUnavailableException("SourceCooldown",state.Blocked);}
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
                using var response = await SendAsync(uri,deadline.Token,jsonBody).ConfigureAwait(false);
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
                // 已逐页核对的工行原件约13.35MiB；仅该精确且有哈希约束的文件可到16MiB，其余仍限8MiB。
                var maximumBytes=OfficialEquityDocuments.IsReviewedIssuer(uri)&&uri.Host=="v.icbc.com.cn"?16*1024*1024:8*1024*1024;
                if(OfficialEtfReportIndex.IsSseRequest(uri)||uri==OfficialEtfReportIndex.SzseEndpoint)maximumBytes=OfficialEtfReportIndex.MaximumPageBytes;
                var body=await CRS.Common.Net.BoundedContentReader.ReadAsync(response.Content,maximumBytes,deadline.Token).ConfigureAwait(false);
                if (status is >= 200 and < 300)
                {
                    lock(policyGate)if(policy.Read(uri.Host).Blocked<=CRS.Timing.BeijingClock.Now)policy.Success(uri.Host);
                    lease?.Complete(status);
                }
                return new(status, body, response.Content.Headers.ContentType?.MediaType);
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
    private async Task<HttpResponseMessage> SendAsync(Uri uri,CancellationToken token,byte[]? jsonBody)
    {
        async Task<HttpResponseMessage> Send(HttpClient transport)
        {
            using var request=new HttpRequestMessage(jsonBody is null?HttpMethod.Get:HttpMethod.Post,uri);
            if(jsonBody is not null){request.Content=new ByteArrayContent(jsonBody);request.Content.Headers.ContentType=new("application/json");}
            // 官方公开页面对应的Referer，不携带Cookie、账号或验证码。
            var activity=uri.Query.Contains("COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",StringComparison.Ordinal) || uri.Query.Contains("scsj_gprdgk_after",StringComparison.Ordinal);
            request.Headers.Referrer=new Uri(uri.Host.EndsWith("sse.com.cn",StringComparison.Ordinal)?activity?"https://www.sse.com.cn/market/stockdata/overview/day/":"https://www.sse.com.cn/market/funddata/volumn/etfvolumn/":activity?"https://www.szse.cn/market/stock/situation/daily/index.html":"https://fund.szse.cn/marketdata/etf/");
            if(uri.Query.Contains("COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",StringComparison.Ordinal))request.Headers.Referrer=new("https://www.sse.com.cn/assortment/stock/list/info/company/index.shtml");
            if(uri.AbsolutePath=="/security/stock/queryCompanyBulletinNew.do")request.Headers.Referrer=new("https://www.sse.com.cn/assortment/stock/list/info/company/index.shtml");
            if(OfficialEtfReportIndex.IsSseRequest(uri))request.Headers.Referrer=new("https://www.sse.com.cn/disclosure/fund/announcement/");
            if(uri==OfficialEtfReportIndex.SzseEndpoint)request.Headers.Referrer=new("https://www.szse.cn/disclosure/fund/notice/index.html");
            if(OfficialEquityDocuments.IsReviewedIssuer(uri))request.Headers.Referrer=OfficialEquityDocuments.IssuerReferrer(uri);
            if(EfundsNavResearch.IsSource(uri))request.Headers.Referrer=new(EfundsNavResearch.ProductUrl);
            if(OfficialIndexSeriesSource.IsSource(uri))request.Headers.Referrer=OfficialIndexSeriesSource.UnitsSource;
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


