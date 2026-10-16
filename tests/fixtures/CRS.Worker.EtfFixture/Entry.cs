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
    private sealed class Session(IBackgroundHostContext context):IBackgroundPluginSession,IParameterizedBackgroundSession,IBackgroundQuerySession,IProgressBackgroundSession
    {
        private readonly NationalTeamBackgroundSession inner=new(context,new Gateway(context.DataDirectory));
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>inner.DescribeJobs();
        public async ValueTask StartAsync(CancellationToken token)
        {
            // 此故障/去重夹具固定五只合成来源；生产默认全目录由仓储配置专项单独验证。
            var path=Path.Combine(context.DataDirectory,"data","market.db");
            if(!File.Exists(path))
            {
                var key=context.OpenDataKey();
                try
                {
                    using(var seed=new CRS.Plugins.NationalTeamWatch.Infrastructure.MarketRepository(Path.GetDirectoryName(path)!,new(Path.Combine(context.DataDirectory,"raw")),encryptionKey:key)){ }
                    using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=path,Password=Convert.ToHexString(key),Pooling=false}.ToString());
                    db.Open();using var command=db.CreateCommand();command.CommandText="UPDATE watchlist_membership SET active=0 WHERE ticker NOT IN ('510300','510050','588000','159919','159915')";command.ExecuteNonQuery();
                }
                finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);}
            }
            await inner.StartAsync(token);
        }
        public Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)=>inner.ExecuteAsync(id,token);
        public Task<BackgroundJobResult> ExecuteAsync(string id,string? payload,CancellationToken token,Guid? commandId=null)=>inner.ExecuteAsync(id,payload,token,commandId);
        public Task<BackgroundJobResult> ExecuteWithProgressAsync(string id,string? payload,Func<BackgroundProgress,Task> report,CancellationToken token,Guid? commandId=null)=>inner.ExecuteWithProgressAsync(id,payload,report,token,commandId);
        public ValueTask StopAsync(CancellationToken token)=>inner.StopAsync(token);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
        public Task<string> QueryAsync(string payload,CancellationToken token)=>payload=="fixture-assemblies"
            ?Task.FromResult(JsonSerializer.Serialize(AppDomain.CurrentDomain.GetAssemblies().Select(x=>x.GetName().Name).ToArray())):inner.QueryAsync(payload,token);
    }
    /// <summary>按请求日期生成标注为测试的公开格式字节，不连接互联网。</summary>
    private sealed class Gateway(string root):IPluginHttpGateway
    {
        public async Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // 仅测试可控的网络等待，验证阶段可查询且配置按插件写入门槛排队。
            while(File.Exists(Path.Combine(root,"http-hold.fixture")))await Task.Delay(25,token);
            var query=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
            var day=query.GetValueOrDefault("SEARCH_DATE")??query.GetValueOrDefault("txtQueryDate")??query.GetValueOrDefault("STAT_DATE")??query["txtStart"];
            var amount=1000+DateOnly.Parse(day).DayNumber;
            if(File.Exists(Path.Combine(root,"changed.fixture")))amount+=100;
            object value;
            if(query.GetValueOrDefault("sqlId")=="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C")
                value=new{sqlId="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C",actionErrors=Array.Empty<string>(),result=new[]{new{TRADE_DATE=day.Replace("-",""),PRODUCT_CODE="17",TRADE_AMT="7804.23",TRADE_VOL="484.13"}}};
            else if(query.GetValueOrDefault("CATALOGID")=="scsj_gprdgk_after")
                value=new[]{new{metadata=new{catalogid="scsj_gprdgk_after",tabkey="tab1",cols=new{gp="深市合计"},conditions=new[]{new{name="txtQueryDate",defaultValue=day}}},
                    data=new[]{new{zbmc="成交金额（亿元）",gp="8,976.31"},new{zbmc="成交量（亿）",gp="562.44"}},error=(string?)null}};
            else if(uri.Host=="query.sse.com.cn")value=new{actionErrors=Array.Empty<string>(),pageHelp=new{pageNo=1,pageCount=1,total=3},
                result=new[]{"510300","510050","588000"}.Select(code=>new{STAT_DATE=day,SEC_CODE=code,TOT_VOL=amount.ToString()}).ToArray()};
            else
            {
                var from=DateOnly.Parse(query["txtStart"]);var to=DateOnly.Parse(query["txtEnd"]);
                var rows=Enumerable.Range(0,to.DayNumber-from.DayNumber+1).Select(i=>from.AddDays(i)).Select(date=>new{size_date=date.ToString("yyyy-MM-dd"),fund_code=query["txtDm"],current_size=(1000+date.DayNumber+(File.Exists(Path.Combine(root,"changed.fixture"))?100:0)).ToString()}).ToArray();
                value=new[]{new{metadata=new{catalogid="fund_jjgm",cols=new{current_size="基金规模<br>(万份)"},recordcount=rows.Length,pagecount=1,pageno=1},data=rows,error=(string?)null}};
            }
            return new PublicHttpResponse(200,JsonSerializer.SerializeToUtf8Bytes(value),"application/json");
        }
    }
}
