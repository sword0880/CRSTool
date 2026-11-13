using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

internal static class SouthboundChecks
{
    private static int passed;
    private static void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 南向年度："+name);}
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("未拒绝："+name);}
    private static readonly DateOnly Through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
    private static readonly DateOnly From=SouthboundAnnualComparison.SourceFrom;
    private static byte[] Body(object[] rows,int? count=null,int? pages=null)=>JsonSerializer.SerializeToUtf8Bytes(new{success=true,code=0,result=new{pages=pages??1,count=count??rows.Length,data=rows}});
    private static object Row(string date,string channel,decimal? buy,decimal? sell=0m,decimal? net=null,decimal? cumulative=null)=>new{MUTUAL_TYPE=channel,TRADE_DATE=date+" 00:00:00",BUY_AMT=buy,SELL_AMT=sell,NET_DEAL_AMT=net??(buy-sell),ACCUM_DEAL_AMT=cumulative};
    private static SouthboundCapture Capture(byte[] body)=>new(From,Through,[new(SouthboundHistorySource.PageUri(From,Through,1).AbsoluteUri,body,DateTimeOffset.UtcNow)]);
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var body=Body([Row("2023-12-29","002",0,cumulative:1000),Row("2023-12-29","004",0,cumulative:2000),
            Row("2024-02-29","002",100,50,cumulative:1050),Row("2024-02-29","004",200,100,cumulative:2100),
            Row("2024-03-01","002",150,350,cumulative:850),Row("2024-03-01","004",250,300,cumulative:2050),
            Row("2024-12-31","002",10,cumulative:860),Row("2024-12-31","004",10,cumulative:2060),
            Row("2025-01-02","002",10,cumulative:870),Row("2025-01-02","004",20,cumulative:2080),
            Row("2025-01-03","002",null,null,0,870),Row("2025-01-03","004",null,null,0,2080),
            Row("2025-03-01","002",500,100,cumulative:1270),Row("2025-03-01","004",600,100,cumulative:2580),
            Row("2025-12-31","002",0,cumulative:1270),Row("2025-12-31","004",0,cumulative:2580),
            Row("2026-01-05","002",700,cumulative:1970),Row("2026-01-06","002",0,cumulative:1970),Row("2026-01-06","004",0,cumulative:2580)]);
        var capture=Capture(body);var rows=SouthboundHistorySource.ParseCapture(capture);
        var report=SouthboundAnnualComparison.Build(2024,2026,Through,DateTimeOffset.UtcNow,"fixture",rows);
        Check(report.FormatVersion==2&&report.Metric==SouthboundAnnualComparison.Metric,"累计净买入具有独立版本和指标身份");
        Check(report.Years[0].Points[0].CumulativeNetBuyYiHkd==1.5m,"沪深买入减卖出合计并换算为亿港元");
        Check(report.Years[0].Points[1].NetBuyYiHkd==-2.5m&&report.Years[0].Points[1].CumulativeNetBuyYiHkd==-1m,"净卖出降低累计值并保留负值");
        Check(report.Years[1].Points[0].CumulativeNetBuyYiHkd==0.3m,"跨年重新起算，不沿用上一年累计");
        Check(report.Years[1].Points[2].CumulativeNetBuyYiHkd==9.3m,"连续多日累计而非单日净买额");
        Check(report.Years[0].Points[1].CalendarDay==report.Years[1].Points.Single(p=>p.Date==new DateOnly(2025,3,1)).CalendarDay,"闰年与普通年三月一日按相同月日对齐");
        Check(report.Years[2].Points.All(p=>p.CumulativeNetBuyYiHkd is null),"缺一路后停止该年累计，不在下一日续算");
        Check(report.Years[1].Points[1].NetBuyYiHkd==0m&&report.Years[1].Points[1].CumulativeNetBuyYiHkd==0.3m,"来源明确公布零净买额可保留，不以空买卖额补零");
        Check(report.Years[2].Points.All(p=>p.Date<=Through)&&report.Years[2].Points.Count==2,"不生成休市日或未来点");
        var noBaseline=SouthboundAnnualComparison.Build(2024,2026,Through,null,null,rows.Where(x=>x.Date.Year!=2023).ToArray());
        Check(noBaseline.Years[0].Points.All(p=>p.CumulativeNetBuyYiHkd is null)&&noBaseline.Years[1].Points[0].CumulativeNetBuyYiHkd==0.3m,"基准不足的年度不可计算，相邻年度保持独立");
        var missingJanuary=SouthboundAnnualComparison.Build(2024,2026,Through,null,null,rows.Where(x=>x.Date!=new DateOnly(2025,1,2)).ToArray());
        Check(missingJanuary.Years[1].Points.All(p=>p.CumulativeNetBuyYiHkd is null)&&missingJanuary.Years[1].Points[0].State=="CumulativeDiscontinuity","来源漏掉年初记录时累计额跳变阻止假累计");
        var missingMiddle=SouthboundAnnualComparison.Build(2024,2026,Through,null,null,rows.Where(x=>x.Date!=new DateOnly(2024,3,1)).ToArray());
        Check(missingMiddle.Years[0].Points[0].CumulativeNetBuyYiHkd==1.5m&&missingMiddle.Years[0].Points[^1].CumulativeNetBuyYiHkd is null,"漏掉中间净卖出日时不连接错误累计");
        var unknown=rows.Select(x=>x.Date==new DateOnly(2025,1,2)&&x.Channel=="004"?x with{BuyMillionHkd=null,SellMillionHkd=null,NetBuyMillionHkd=null}:x).ToArray();
        Check(SouthboundAnnualComparison.Build(2024,2026,Through,null,null,unknown).Years[1].Points.All(p=>p.CumulativeNetBuyYiHkd is null),"未知净买额不作为零继续累计");
        var noCumulative=rows.Select(x=>x.Date==new DateOnly(2025,1,2)&&x.Channel=="004"?x with{CumulativeNetBuyMillionHkd=null}:x).ToArray();
        Check(SouthboundAnnualComparison.Build(2024,2026,Through,null,null,noCumulative).Years[1].Points.All(p=>p.CumulativeNetBuyYiHkd is null),"累计核对字段缺失时不把逐日求和冒充已核连续累计");
        var derived=rows.Select(x=>x.Date==new DateOnly(2024,2,29)?x with{NetBuyMillionHkd=null}:x).ToArray();
        Check(SouthboundAnnualComparison.Build(2024,2026,Through,null,null,derived).Years[0].Points[0].CumulativeNetBuyYiHkd==1.5m,"买卖额齐备时可计算净买入，不要求补造公布字段");
        var partialBaseline=rows.Where(x=>!(x.Date==new DateOnly(2024,12,31)&&x.Channel=="004")).ToArray();
        Check(SouthboundAnnualComparison.Build(2024,2026,Through,null,null,partialBaseline).Years[1].Points.All(p=>p.State=="MissingYearBaseline"),"跨年基准两通道日期必须齐备，不借用不同日期拼接");
        Reject(()=>SouthboundAnnualComparison.Build(2016,2026,Through,null,null,[]),"超出年度范围拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",1),Row("2025-01-02","002",2)]))),"重复通道日期拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","001",1)]))),"北向通道拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",-1)]))),"负买入金额拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",100,20,90,1000)]))),"公布净买额与买入减卖出矛盾拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",1)],2001,2))),"分页提前结束拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(capture with{Pages=[capture.Pages[0] with{Url="https://example.com/history"}]}),"任意来源地址拒绝");
        Check(SouthboundHistorySource.IsSource(new(capture.Pages[0].Url)),"固定历史查询通过来源白名单");
        Check(!SouthboundHistorySource.IsSource(new(capture.Pages[0].Url+"&extra=1")),"追加未知参数拒绝");
        Check(!SouthboundHistorySource.IsSource(new(capture.Pages[0].Url.Replace("BUY_AMT","SELL_AMT",StringComparison.Ordinal))),"更换数据字段拒绝");
        var id=Guid.NewGuid();string fingerprint;
        var legacyFile=Path.Combine(root,"data","market.db");
        var writes=0;
        using(var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,()=>writes++))
        {
            Check(repo.SouthboundAnnual(2024,2026).Years.All(y=>y.Points.Count==0),"无登记数据保持空白");
            using(var legacy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=legacyFile}.ToString()))
            {legacy.Open();using var cmd=legacy.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS nt_southbound_capture(id TEXT PRIMARY KEY,from_date TEXT,through_date TEXT,registered_at TEXT,manifest TEXT); INSERT INTO nt_southbound_capture VALUES('legacy-buy','2017-01-01','2026-11-03','2026-11-03','{}');";cmd.ExecuteNonQuery();}
            Check(repo.SouthboundAnnual(2024,2026).Years.All(y=>y.Points.Count==0),"旧版仅买入快照不冒充累计净买入资料");
            fingerprint=repo.SaveSouthbound(capture,id);
            Check(repo.SaveSouthbound(capture,id)==fingerprint,"同请求重放不重复登记");
            var before=writes;var read=repo.Read(r=>((ISouthboundReadRepository)r).SouthboundAnnual(2024,2026),default);
            Check(read.CaptureId==fingerprint&&read.Years[0].Points[0].CumulativeNetBuyYiHkd==1.5m,"独立只读快照回读累计净买入存证");
            Check(writes==before,"年度查询不申请写入，且通过独立只读连接完成");
            Reject(()=>repo.SaveSouthbound(capture with{Pages=[capture.Pages[0] with{Content="broken"u8.ToArray()}]},Guid.NewGuid()),"采集失败不覆盖上一份快照");
            Check(repo.SouthboundAnnual(2024,2026).CaptureId==fingerprint,"失败后仍保留上一份完整数据");
        }
        using(var reopened=TestMarketData.Open(Path.Combine(root,"data"),raw))Check(reopened.SouthboundAnnual(2024,2026).CaptureId==fingerprint,"重启后快照身份与曲线保留");
        using(var legacy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=legacyFile,Mode=SqliteOpenMode.ReadOnly}.ToString()))
        {legacy.Open();using var cmd=legacy.CreateCommand();cmd.CommandText="SELECT count(*) FROM nt_southbound_capture WHERE id='legacy-buy'";Check(Convert.ToInt32(cmd.ExecuteScalar())==1,"新版本登记及重启均保留旧版仅买入快照");}
        var context=new Context(Path.Combine(root,"session"));var gateway=new Fixture(body);await using(var session=new NationalTeamBackgroundSession(context,gateway))
        {
            Check(session.DescribeJobs().Select(d=>session.ExecutionGroup(d.JobId)).GroupBy(g=>g.Id).All(g=>g.Select(x=>x.MaximumConcurrency).Distinct().Count()==1),"同一执行组并发声明一致，可通过宿主加载门槛");
            await session.StartAsync(default);var request=Guid.NewGuid();var payload=EtfBusinessProtocol.WriteRequest(new("southbound-net-sync"));
            Check(session.QueryOperation(EtfBusinessProtocol.WriteRequest(new("southbound-annual")))=="unknown","旧单日买入查询不会返回新指标冒充兼容");
            Check((await session.ExecuteAsync("southbound-sync",payload,default,request)).Status=="Succeeded","正式后台处理器接受完整快照");
            Check((await session.ExecuteAsync("southbound-sync",payload,default,request)).Status=="Succeeded"&&gateway.Calls==1,"同一请求不重派HTTP");
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(new("southbound-net-annual",From:new(2024,1,1),To:new(2026,12,31),Count:256)),default));
            var result=CRS.Serialization.JsonDocumentBlocks.Decode<SouthboundAnnualReport>(JsonSerializer.Deserialize<CRS.Serialization.JsonDocumentBlock[]>(page.Items)!,default);
            Check(result.Years[0].Points[0].CumulativeNetBuyYiHkd==1.5m&&page.NextOffset is null,"公开查询完整返回累计净买入报告");
            gateway.Fail=true;Check((await session.ExecuteAsync("southbound-sync",payload,default,Guid.NewGuid())).Status=="RetryDeferred","来源失败为等待恢复，不能冒充成功");
        }
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));File.WriteAllText(Path.Combine(root,"raw",hash+".raw"),"tampered");
        using(var damaged=TestMarketData.Open(Path.Combine(root,"data"),raw))Reject(()=>damaged.SouthboundAnnual(2024,2026),"原件损坏拒绝输出旧金额");
        Console.WriteLine($"PASS 南向年度共 {passed} 项；合成隔离数据，未修改正式服务。");
    }
    public static async Task CaptureLiveAsync(string root)
    {
        Directory.CreateDirectory(root);var context=new Context(Path.Combine(root,"plugin"));
        await using(var session=new NationalTeamBackgroundSession(context))
        {
            await session.StartAsync(default);var result=await session.ExecuteAsync("southbound-sync",EtfBusinessProtocol.WriteRequest(new("southbound-net-sync")),default,Guid.NewGuid());
            if(result.Status!="Succeeded")throw new InvalidDataException("真实来源未完成："+result.Status+" "+result.Checkpoint);
        }
        ReadCaptured(root);
    }
    public static void ReadCaptured(string root)
    {
        var context=new Context(Path.Combine(root,"plugin"));var raw=new RawEvidenceStore(Path.Combine(context.DataDirectory,"raw"));using var repo=TestMarketData.Open(Path.Combine(context.DataDirectory,"data"),raw);
        var report=repo.Read(r=>((ISouthboundReadRepository)r).SouthboundAnnual(2022,2026),default);
        File.WriteAllText(Path.Combine(root,"annual-report.json"),JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(new{Installed=false,report.Metric,report.CaptureId,report.Through,Years=report.Years.Select(y=>new{y.Year,y.BaselineDate,Days=y.Points.Count,Valid=y.Points.Count(p=>p.CumulativeNetBuyYiHkd.HasValue),Last=y.Points.LastOrDefault()})}));
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class Fixture(byte[] body):IPluginHttpGateway
    {
        public int Calls;public bool Fail;
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token){token.ThrowIfCancellationRequested();if(!SouthboundHistorySource.IsSource(uri))throw new InvalidDataException("来源未登记");Calls++;return Task.FromResult(new PublicHttpResponse(Fail?503:200,Fail?[]:body,"application/json"));}
    }
}
