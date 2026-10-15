using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
namespace CRS.Worker.EtfFixture;
/// <summary>合成来源通过真实后台入口注入，仅替换 HTTP，不替换采集、存储或调度实现。</summary>
public sealed class Entry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException();
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new Session(context);
    private sealed class Session(IBackgroundHostContext context):IBackgroundPluginSession,IParameterizedBackgroundSession,IBackgroundQuerySession
    {
        private readonly NationalTeamBackgroundSession inner=new(context,new Gateway(context.DataDirectory));
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>inner.DescribeJobs();
        public ValueTask StartAsync(CancellationToken token)=>inner.StartAsync(token);
        public Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)=>inner.ExecuteAsync(id,token);
        public Task<BackgroundJobResult> ExecuteAsync(string id,string? payload,CancellationToken token,Guid? commandId=null)=>inner.ExecuteAsync(id,payload,token,commandId);
        public ValueTask StopAsync(CancellationToken token)=>inner.StopAsync(token);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
        public Task<string> QueryAsync(string payload,CancellationToken token)=>payload=="fixture-assemblies"
            ?Task.FromResult(JsonSerializer.Serialize(AppDomain.CurrentDomain.GetAssemblies().Select(x=>x.GetName().Name).ToArray())):inner.QueryAsync(payload,token);
    }
    /// <summary>按请求日期生成标注为测试的公开格式字节，不连接互联网。</summary>
    private sealed class Gateway(string root):IPluginHttpGateway
    {
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var query=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
            var day=query.GetValueOrDefault("STAT_DATE")??query["txtStart"];
            var amount=1000+DateOnly.Parse(day).DayNumber;
            if(File.Exists(Path.Combine(root,"changed.fixture")))amount+=100;
            object value;
            if(uri.Host=="query.sse.com.cn")value=new{actionErrors=Array.Empty<string>(),pageHelp=new{pageNo=1,pageCount=1,total=3},
                result=new[]{"510300","510050","588000"}.Select(code=>new{STAT_DATE=day,SEC_CODE=code,TOT_VOL=amount.ToString()}).ToArray()};
            else value=new[]{new{metadata=new{catalogid="fund_jjgm",cols=new{current_size="基金规模<br>(万份)"},recordcount=1,pagecount=1,pageno=1},
                data=new[]{new{size_date=day,fund_code=query["txtDm"],current_size=amount.ToString()}},error=(string?)null}};
            return Task.FromResult(new PublicHttpResponse(200,JsonSerializer.SerializeToUtf8Bytes(value),"application/json"));
        }
    }
}
