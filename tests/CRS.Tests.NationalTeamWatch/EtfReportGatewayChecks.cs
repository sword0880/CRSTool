using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class EtfReportGatewayChecks
{
    internal static async Task RunAsync()
    {
        var policy=new Policy();var sent=0;
        using var gateway=new OfficialHttpGateway(policy,new Handler((request,token)=>
        {sent++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{}")});}));
        var key=new EtfKey("SZSE","159915");var body=OfficialEtfReportIndex.RequestBody(key,new(2026,1,1),new(2026,10,27),1)!;
        await Reject<InvalidDataException>(()=>gateway.PostJsonAsync(new("https://www.szse.cn/api/other"),body,default),"POST只允许明确的官方基金查询地址");
        await Reject<InvalidDataException>(()=>gateway.PostJsonAsync(OfficialEtfReportIndex.SzseEndpoint,new byte[4097],default),"POST字段容量在联网前拒绝");
        await Reject<InvalidDataException>(()=>gateway.PostJsonAsync(OfficialEtfReportIndex.SzseEndpoint,Encoding.UTF8.GetBytes("{}"),default),"缺少字段在联网前拒绝");
        Check(sent==0&&policy.Reservations==0,"非法POST不消耗来源额度或触发HTTP");
        Check((await gateway.PostJsonAsync(OfficialEtfReportIndex.SzseEndpoint,body,default)).StatusCode==403,"403返回实际拒绝状态");
        await Reject<InvalidOperationException>(()=>gateway.GetAsync(OfficialEtfReportIndex.SzseEndpoint,default),"POST403后来源冷却仍阻止GET请求");
        Check(sent==1&&policy.Failures==1,"HTTP拒绝不尝试直连绕过，GET/POST共用来源策略");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? received=null;bool headers=false;
        using var freezing=new OfficialHttpGateway(new Policy(),new Handler(async(request,token)=>
        {
            started.SetResult();await resume.Task.WaitAsync(token);received=await request.Content!.ReadAsStringAsync(token);
            headers=request.Method==HttpMethod.Post&&request.Headers.Referrer?.AbsoluteUri=="https://www.szse.cn/disclosure/fund/notice/index.html"&&request.Content.Headers.ContentType?.MediaType=="application/json";
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}")};
        }));
        var expected=Encoding.UTF8.GetString(body);var task=freezing.PostJsonAsync(OfficialEtfReportIndex.SzseEndpoint,body,default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));Array.Fill(body,(byte)'x');resume.SetResult();await task;
        Check(received==expected&&headers,"POST冻结原参数并使用正确官方Referer与JSON类型");
        Console.WriteLine("ETF报告网关8项通过；固定传输，不访问外网。");
    }
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS ETF报告网关："+label);}
    private static async Task Reject<T>(Func<Task> work,string label) where T:Exception
    {try{await work();}catch(T){Check(true,label);return;}throw new Exception(label);}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
    private sealed class Policy:ISourceRequestPolicy
    {
        private DateTimeOffset blocked;public int Reservations,Failures;
        public (DateTimeOffset Next,DateTimeOffset Blocked,int Failures) Read(string host)=>(DateTimeOffset.MinValue,blocked,Failures);
        public void Reserve(string host,DateTimeOffset now)=>Reservations++;
        public void Success(string host){blocked=DateTimeOffset.MinValue;}
        public void Failure(string host,DateTimeOffset now,int status,DateTimeOffset? retryAfter=null){Failures++;blocked=now.AddHours(6);}
        public void Dispose(){}
    }
}
