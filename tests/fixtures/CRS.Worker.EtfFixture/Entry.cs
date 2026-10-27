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
    private sealed class Session(IBackgroundHostContext context):IBackgroundPluginSession,IParameterizedBackgroundSession,IBackgroundQuerySession,IProgressBackgroundSession,IGroupedBackgroundSession
    {
        private readonly NationalTeamBackgroundSession inner=new(context,new Gateway(context.DataDirectory),indexSourceApprovals:new IndexApprovals(context.DataDirectory));
        public int MaximumConcurrentExecutions=>inner.MaximumConcurrentExecutions;
        public BackgroundExecutionGroup ExecutionGroup(string jobId)=>inner.ExecutionGroup(jobId);
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>inner.DescribeJobs().Select(x=>Directory.Exists(Path.Combine(context.DataDirectory,"historical-pdf.fixture"))||Directory.Exists(Path.Combine(context.DataDirectory,"etf-holders.fixture"))||Directory.Exists(Path.Combine(context.DataDirectory,"etf-reports.fixture"))||x.JobId is "official-calendar-sync" or "equity-disclosure-sync"?x with{EnabledByDefault=false,RunOnStartup=false}:x).ToArray();
        public async ValueTask StartAsync(CancellationToken token)
        {
            // 此故障/去重夹具固定五只合成来源；生产默认全目录由仓储配置专项单独验证。
            var path=Path.Combine(context.DataDirectory,"data","market.db");
            if(!File.Exists(path))
            {
                var key=context.OpenDataKey();
                try
                {
                    using(var seed=new CRS.Plugins.NationalTeamWatch.Infrastructure.MarketRepository(Path.GetDirectoryName(path)!,new(Path.Combine(context.DataDirectory,"raw")),approvalAuditKey:key))
                    {
                        // 仅行业管道夹具显式携带合成休市公告；沿生产解析器登记，不让其它测试凭空获得日历。
                        var calendar=Path.Combine(context.DataDirectory,"industry-pe.fixture","calendar-2026.html");
                        if(File.Exists(calendar))seed.RegisterReviewedCalendar("SZSE",2026,File.ReadAllBytes(calendar),CRS.Plugins.NationalTeamWatch.Domain.ReviewedExchangeCalendar.Source("SZSE",2026));
                    }
                    using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
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
    /// <summary>仅测试包读取显式合成许可标记；生产发行不含此入口，不把公开原件替身算作真实来源授权。</summary>
    private sealed class IndexApprovals(string root):CRS.Plugins.NationalTeamWatch.Application.IIndexSeriesSourceApprovals
    {
        private string PathName=>Path.Combine(root,"index-series.fixture","synthetic-terms.txt");
        public CRS.Plugins.NationalTeamWatch.Application.MarketCycleSourceApproval? Find(string dataset)
        {
            if(!File.Exists(PathName))return null;
            return new("CNINDEX",new("https://www.cnindex.com.cn/"),dataset,"cnindex-index-series-v1","cnindex-index-series-v1",
                CRS.Plugins.NationalTeamWatch.Application.MarketCycleSourceApprovalStatus.Approved,
                CRS.Plugins.NationalTeamWatch.Application.MarketCycleSourceUse.AutomatedCollection|CRS.Plugins.NationalTeamWatch.Application.MarketCycleSourceUse.PersistentStorage|CRS.Plugins.NationalTeamWatch.Application.MarketCycleSourceUse.HistoricalBackfill,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(PathName))),"SyntheticFixture",DateTimeOffset.UtcNow.AddMinutes(-1),"synthetic-v1");
        }
        public bool Verify(string hash)=>File.Exists(PathName)&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(PathName)))==hash;
    }
    /// <summary>按请求日期生成标注为测试的公开格式字节，不连接互联网。</summary>
    private sealed class Gateway(string root):IPluginHttpGateway,IPluginJsonHttpGateway
    {
        public async Task<PublicHttpResponse> PostJsonAsync(Uri uri,byte[] body,CancellationToken token)
        {
            var path=Path.Combine(root,"etf-reports.fixture");
            if(!Directory.Exists(path))throw new InvalidDataException("本夹具没有开启ETF目录POST。");
            CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialEtfReportIndex.ValidatePost(uri,body);
            using var expected=JsonDocument.Parse(CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialEtfReportIndex.RequestBody(new("SZSE","159915"),new(2026,1,1),new(2026,10,27),1)!);
            using var received=JsonDocument.Parse(body);
            if(!JsonElement.DeepEquals(expected.RootElement,received.RootElement))throw new InvalidDataException("ETF目录夹具POST参数超出已保存响应范围。");
            await File.AppendAllTextAsync(Path.Combine(path,"calls.log"),"SZSE\n",token);
            return new(200,await File.ReadAllBytesAsync(Path.Combine(path,"szse.json"),token),"application/json");
        }
        public async Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var reports=Path.Combine(root,"etf-reports.fixture");
            if(Directory.Exists(reports)&&CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialEtfReportIndex.IsSseRequest(uri))
            {
                if(uri!=CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialEtfReportIndex.Request(new("SSE","510300"),new(2026,1,1),new(2026,10,27),1))throw new InvalidDataException("ETF目录夹具GET参数超出已保存响应范围。");
                await File.AppendAllTextAsync(Path.Combine(reports,"calls.log"),"SSE\n",token);
                return new(200,await File.ReadAllBytesAsync(Path.Combine(reports,"sse.json"),token),"application/json");
            }
            var holders=Path.Combine(root,"etf-holders.fixture");
            if(Directory.Exists(holders))
            {
                var year=uri.AbsoluteUri switch
                {
                    "https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-03-31/510300_20260331_W6CZ.pdf"=>2025,
                    "https://www.sse.com.cn/disclosure/fund/announcement/c/new/2025-03-31/510300_20250331_07Z1.pdf"=>2024,
                    _=>throw new InvalidDataException("持有人夹具出现未列来源。")
                };
                await File.AppendAllTextAsync(Path.Combine(holders,"calls.log"),year+"\n",token);
                return new(200,await File.ReadAllBytesAsync(Path.Combine(holders,$"510300-{year}.pdf"),token),"application/pdf");
            }
            var historical=Path.Combine(root,"historical-pdf.fixture");
            if(Directory.Exists(historical))
            {
                // 历史披露完整管道只回放已核原件；意外地址直接失败，不让夹具请求真实网络。
                if(uri.AbsoluteUri!="https://www.chinacnd.com/report-226.html")throw new InvalidDataException("历史夹具出现未列来源。");
                await File.AppendAllTextAsync(Path.Combine(historical,"calls.log"),"original\n",token);
                return new(200,await File.ReadAllBytesAsync(Path.Combine(historical,"original.pdf"),token),"application/pdf");
            }
            // 仅测试可控的网络等待，验证阶段可查询且配置按插件写入门槛排队。
            while(File.Exists(Path.Combine(root,"http-hold.fixture")))await Task.Delay(25,token);
            // NAV管道验收只读取预先核对的公开原件；此夹具不进入正式发行，也不请求网络。
            var nav=CRS.Plugins.NationalTeamWatch.Infrastructure.EfundsNavResearch.OfficialUrls.ToList().IndexOf(uri.AbsoluteUri);
            if(nav>=0)return new(200,await File.ReadAllBytesAsync(Path.Combine(root,"nav-source.fixture",nav+".raw"),token),"application/javascript");
            // 指数完整管道仅回放冻结文件；任何未列地址仍走原夹具，不连接外部网络。
            if(CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialIndexSeriesSource.IsSource(uri))
            {
                var source=CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialIndexSeriesSource.UnitsSource;
                var name=uri==source?"index-detail.html":uri==CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialIndexSeriesSource.DaySource?"query-day.json":uri==CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialIndexSeriesSource.ValuationSource?"industry-index-list-1.json":uri.AbsolutePath.EndsWith("gz_399317.pdf",StringComparison.Ordinal)?"method-a.pdf":uri.AbsolutePath.EndsWith("gz_399381.pdf",StringComparison.Ordinal)?"method-sector.pdf":System.Web.HttpUtility.ParseQueryString(uri.Query)["indexCode"] is "399317"?"cni-a-20261020-23.json":System.Web.HttpUtility.ParseQueryString(uri.Query)["indexCode"]+"-4days.json";
                return new(200,await File.ReadAllBytesAsync(Path.Combine(root,"index-series.fixture",name),token),"application/octet-stream");
            }
            if(CRS.Plugins.NationalTeamWatch.Infrastructure.OfficialIndustryPeSource.IsSource(uri))return new(200,await File.ReadAllBytesAsync(Path.Combine(root,"industry-pe.fixture",uri.AbsolutePath.Split('/')[2]+".json"),token),"application/json");
            var query=uri.Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>Uri.UnescapeDataString(x[1]));
            if(uri.AbsolutePath=="/security/stock/queryCompanyBulletinNew.do")
            {
                var code=query["SECURITY_CODE"];var date=query["END_DATE"];
                var valueReport=new{SECURITY_CODE=code,START_DATE=query["START_DATE"],END_DATE=date,TITLE="报告",pageHelp=new{total=1,pageCount=1,pageNo=1,pageSize=10},result=new[]{new[]{new{SECURITY_CODE=code,ORG_BULLETIN_ID="fixture-report",TITLE="合成2026年半年度报告",SSEDATE=date,BULLETIN_TYPE_DESC="半年报",URL=$"/disclosure/listedinfo/announcement/c/new/{date}/{code}_{date.Replace("-","")}_FIXTURE.pdf"}}}};
                return new PublicHttpResponse(200,JsonSerializer.SerializeToUtf8Bytes(valueReport),"application/json");
            }
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
