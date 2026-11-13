using System.Net;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>使用真实加密来源策略与替身HTTP，检查国证冷却持久化和窄路径；不请求真实源站。</summary>
internal static class IndustrySourceBudgetChecks
{
    public static async Task RunAsync(string root,Action<bool,string> check)
    {
        var path=Path.Combine(root,"source-policy");var key=RandomNumberGenerator.GetBytes(32);var handler=new Handler();var uri=OfficialIndustryPeSource.Source(new(2026,10,23));
        using(var policy=new SourceRequestPolicy(path))
        using(var gateway=new OfficialHttpGateway(policy,handler))
        {
            var response=await gateway.GetAsync(uri,default);check(response.StatusCode==403&&handler.Calls==1,"国证HTTP拒绝真实经过受限网关");
            check(policy.Read("www.cnindex.com.cn").Blocked>DateTimeOffset.UtcNow.AddHours(5.9),"国证403形成至少六小时持久冷却");
            check(policy.Read("www.szse.cn").Blocked==DateTimeOffset.MinValue&&policy.Read("www.efunds.com.cn").Blocked==DateTimeOffset.MinValue,"行业来源冷却不污染交易所或基金预算");
            try{await gateway.GetAsync(uri,default);throw new InvalidDataException("冷却未阻断");}catch(InvalidOperationException){check(handler.Calls==1,"冷却期间不再次访问源站");}
            try{await gateway.GetAsync(new("https://www.cnindex.com.cn/unknown.json"),default);throw new InvalidDataException("任意路径未阻断");}catch(InvalidOperationException){check(handler.Calls==1,"授权域名不能扩大为任意路径下载");}
        }
        using(var reopened=new SourceRequestPolicy(path))check(reopened.Read("www.cnindex.com.cn").Blocked>DateTimeOffset.UtcNow.AddHours(5.9),"来源策略重启仍保留国证拒绝期限");
    }
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("fixture-forbidden")});}
    }
}
