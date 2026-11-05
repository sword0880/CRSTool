using System.Net;
using System.Text;
using System.Text.Json;
using CRS.Infrastructure;

/// <summary>阻塞真实HTTP适配器的合成传输，验证并行、正文准入、失败取消和稳定冻结。</summary>
internal static class PerformanceMarketConcurrencyChecks
{
    private const string Json="""{"chart":{"result":[{"meta":{"exchangeTimezoneName":"UTC"},"timestamp":[1735776000,1735862400],"indicators":{"quote":[{"close":[100,110]}]}}]}}""";
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value,string name)
    {if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS 行情并发："+name);}
    public static async Task RunAsync()
    {
        await ParallelAndStable();await Cancel();await FailedSibling();await BodyAdmission();
    }
    private static async Task ParallelAndStable()
    {
        var both=Signal();var release=Signal();var calls=0;
        using var http=new HttpClient(new Handler(async(request,token)=>
        {if(Interlocked.Increment(ref calls)==2)both.TrySetResult();await release.Task.WaitAsync(token);return Response();}));
        var running=new YahooPerformanceMarketData(http).FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"CNY",default);
        try{await both.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(!running.IsCompleted,"指数与换汇响应同时在途");}
        finally{release.TrySetResult();}
        var document=await running.WaitAsync(TimeSpan.FromSeconds(5));
        using var frozen=JsonDocument.Parse(document.Raw);
        Check(frozen.RootElement.GetProperty("Responses").EnumerateObject().Select(p=>p.Name).SequenceEqual(["^GSPC","USDCNY=X"])
            &&document.Hash==PerformanceCodec.Hash(document.Raw)&&document.Data.Prices.Length==4,"完成次序不改变指数在前、汇率在后的冻结来源及摘要");
        calls=0;await new YahooPerformanceMarketData(http).FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"USD",default);
        Check(calls==1,"同币种不创建多余汇率请求");
    }
    private static async Task Cancel()
    {
        var both=Signal();var calls=0;var active=0;using var stop=new CancellationTokenSource();
        using var http=new HttpClient(new Handler(async(request,token)=>
        {
            Interlocked.Increment(ref active);
            try{if(Interlocked.Increment(ref calls)==2)both.TrySetResult();await Task.Delay(Timeout.Infinite,token);return Response();}
            finally{Interlocked.Decrement(ref active);}
        }));
        var running=new YahooPerformanceMarketData(http).FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"CNY",stop.Token);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();
        try{await running.WaitAsync(TimeSpan.FromSeconds(5));throw new InvalidOperationException("用户取消未生效。");}catch(OperationCanceledException){}
        Check(active==0,"用户取消同时终止指数和汇率，返回前排空请求");
    }
    private static async Task FailedSibling()
    {
        var both=Signal();var calls=0;var cancelled=0;
        using var http=new HttpClient(new Handler(async(request,token)=>
        {
            if(Interlocked.Increment(ref calls)==2)both.TrySetResult();await both.Task.WaitAsync(token);
            if(request.RequestUri!.AbsolutePath.Contains("GSPC",StringComparison.Ordinal))return new(HttpStatusCode.ServiceUnavailable);
            try{await Task.Delay(Timeout.Infinite,token);return Response();}catch(OperationCanceledException){Interlocked.Increment(ref cancelled);throw;}
        }));
        try{await new YahooPerformanceMarketData(http).FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"CNY",default).WaitAsync(TimeSpan.FromSeconds(5));throw new InvalidOperationException("来源失败应拒绝整份报告。");}
        catch(CRS.Domain.CrsException){}
        Check(cancelled==1,"一个来源失败立即取消同次查询的另一请求，不返回部分报告");
    }
    private static async Task BodyAdmission()
    {
        var four=Signal();var release=Signal();var headers=0;
        using var http=new HttpClient(new Handler((request,token)=>
        {
            if(Interlocked.Increment(ref headers)==4)four.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new PausedBody(release,Encoding.UTF8.GetBytes(Json)))});
        }));
        var requests=Enumerable.Range(0,6).Select(_=>new YahooPerformanceMarketData(http).FetchAsync("SPX",new(2025,1,2),new(2025,1,3),"CNY",default)).ToArray();
        try
        {
            await four.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(100);
            Check(headers==4,"六个实例共用最多4个在途HTTP额度，正文未读完不释放额度");
        }
        finally{release.TrySetResult();}
        var documents=await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        Check(headers==12&&documents.All(d=>d.Data.Prices.Length==4),"排队请求在正文完成后推进，所有报告完整");
    }
    private static HttpResponseMessage Response()=>new(HttpStatusCode.OK){Content=new StringContent(Json)};
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> next):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>next(request,token);}
    private sealed class PausedBody(TaskCompletionSource release,byte[] bytes):MemoryStream(bytes,false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {await release.Task.WaitAsync(token);return await base.ReadAsync(buffer,token);}
    }
}
