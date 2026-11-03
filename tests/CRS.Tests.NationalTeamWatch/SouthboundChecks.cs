using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;

internal static class SouthboundChecks
{
    private static int passed;
    private static void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 南向年度："+name);}
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("未拒绝："+name);}
    private static readonly DateOnly Through=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
    private static readonly DateOnly From=new(2017,1,1);
    private static byte[] Body(object[] rows,int? count=null,int? pages=null)=>JsonSerializer.SerializeToUtf8Bytes(new{success=true,code=0,result=new{pages=pages??1,count=count??rows.Length,data=rows}});
    private static object Row(string date,string channel,decimal? amount)=>new{MUTUAL_TYPE=channel,TRADE_DATE=date+" 00:00:00",BUY_AMT=amount};
    private static SouthboundCapture Capture(byte[] body)=>new(From,Through,[new(SouthboundHistorySource.PageUri(From,Through,1).AbsoluteUri,body,DateTimeOffset.UtcNow)]);
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var body=Body([Row("2024-02-29","002",100),Row("2024-02-29","004",200),Row("2024-03-01","002",150),Row("2024-03-01","004",250),Row("2025-03-01","002",500),Row("2025-03-01","004",600),Row("2026-01-05","002",700),Row("2026-01-06","002",800),Row("2026-01-06","004",null),Row("2026-01-07","002",0),Row("2026-01-07","004",0)]);
        var capture=Capture(body);var rows=SouthboundHistorySource.ParseCapture(capture);
        var report=SouthboundAnnualComparison.Build(2024,2026,Through,DateTimeOffset.UtcNow,"fixture",rows);
        Check(report.Years[0].Points[0].BuyYiHkd==3m,"百万港元准确合计并换算为亿港元");
        Check(report.Years[0].Points[1].CalendarDay==report.Years[1].Points[0].CalendarDay,"闰年与普通年三月一日按相同月日对齐");
        Check(report.Years[2].Points.Take(2).All(p=>p.BuyYiHkd is null),"缺一路和空金额均不冒充合计");
        Check(report.Years[2].Points[^1].BuyYiHkd==0m,"有来源的真实零金额保留");
        Check(report.Years[2].Points.All(p=>p.Date<=Through)&&report.Years[2].Points.Count==3,"不生成休市日或未来点");
        Reject(()=>SouthboundAnnualComparison.Build(2016,2026,Through,null,null,[]),"超出年度范围拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",1),Row("2025-01-02","002",2)]))),"重复通道日期拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","001",1)]))),"北向通道拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",-1)]))),"负买入金额拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(Capture(Body([Row("2025-01-02","002",1)],2001,2))),"分页提前结束拒绝");
        Reject(()=>SouthboundHistorySource.ParseCapture(capture with{Pages=[capture.Pages[0] with{Url="https://example.com/history"}]}),"任意来源地址拒绝");
        Check(SouthboundHistorySource.IsSource(new(capture.Pages[0].Url)),"固定历史查询通过来源白名单");
        Check(!SouthboundHistorySource.IsSource(new(capture.Pages[0].Url+"&extra=1")),"追加未知参数拒绝");
        Check(!SouthboundHistorySource.IsSource(new(capture.Pages[0].Url.Replace("BUY_AMT","SELL_AMT",StringComparison.Ordinal))),"更换数据字段拒绝");
        var id=Guid.NewGuid();string fingerprint;
        var writes=0;
        using(var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,()=>writes++))
        {
            Check(repo.SouthboundAnnual(2024,2026).Years.All(y=>y.Points.Count==0),"无登记数据保持空白");
            fingerprint=repo.SaveSouthbound(capture,id);
            Check(repo.SaveSouthbound(capture,id)==fingerprint,"同请求重放不重复登记");
            var before=writes;var read=repo.Read(r=>((ISouthboundReadRepository)r).SouthboundAnnual(2024,2026),default);
            Check(read.CaptureId==fingerprint&&read.Years[0].Points[0].BuyYiHkd==3m,"独立只读快照回读真实存证");
            Check(writes==before,"年度查询不申请写入，且通过独立只读连接完成");
            Reject(()=>repo.SaveSouthbound(capture with{Pages=[capture.Pages[0] with{Content="broken"u8.ToArray()}]},Guid.NewGuid()),"采集失败不覆盖上一份快照");
            Check(repo.SouthboundAnnual(2024,2026).CaptureId==fingerprint,"失败后仍保留上一份完整数据");
        }
        using(var reopened=TestMarketData.Open(Path.Combine(root,"data"),raw))Check(reopened.SouthboundAnnual(2024,2026).CaptureId==fingerprint,"重启后快照身份与曲线保留");
        var context=new Context(Path.Combine(root,"session"));var gateway=new Fixture(body);await using(var session=new NationalTeamBackgroundSession(context,gateway))
        {
            Check(session.DescribeJobs().Select(d=>session.ExecutionGroup(d.JobId)).GroupBy(g=>g.Id).All(g=>g.Select(x=>x.MaximumConcurrency).Distinct().Count()==1),"同一执行组并发声明一致，可通过宿主加载门槛");
            await session.StartAsync(default);var request=Guid.NewGuid();var payload=EtfBusinessProtocol.WriteRequest(new("southbound-sync"));
            Check((await session.ExecuteAsync("southbound-sync",payload,default,request)).Status=="Succeeded","正式后台处理器接受完整快照");
            Check((await session.ExecuteAsync("southbound-sync",payload,default,request)).Status=="Succeeded"&&gateway.Calls==1,"同一请求不重派HTTP");
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(new("southbound-annual",From:new(2024,1,1),To:new(2026,12,31),Count:256)),default));
            var result=CRS.Serialization.JsonDocumentBlocks.Decode<SouthboundAnnualReport>(JsonSerializer.Deserialize<CRS.Serialization.JsonDocumentBlock[]>(page.Items)!,default);
            Check(result.Years[0].Points[0].BuyYiHkd==3m&&page.NextOffset is null,"公开查询完整返回年度报告");
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
            await session.StartAsync(default);var result=await session.ExecuteAsync("southbound-sync",EtfBusinessProtocol.WriteRequest(new("southbound-sync")),default,Guid.NewGuid());
            if(result.Status!="Succeeded")throw new InvalidDataException("真实来源未完成："+result.Status+" "+result.Checkpoint);
        }
        ReadCaptured(root);
    }
    public static void ReadCaptured(string root)
    {
        var context=new Context(Path.Combine(root,"plugin"));var raw=new RawEvidenceStore(Path.Combine(context.DataDirectory,"raw"));using var repo=TestMarketData.Open(Path.Combine(context.DataDirectory,"data"),raw);
        var report=repo.Read(r=>((ISouthboundReadRepository)r).SouthboundAnnual(2022,2026),default);
        File.WriteAllText(Path.Combine(root,"annual-report.json"),JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(new{Installed=false,report.CaptureId,report.Through,Years=report.Years.Select(y=>new{y.Year,Days=y.Points.Count,Valid=y.Points.Count(p=>p.BuyYiHkd.HasValue),Last=y.Points.LastOrDefault()})}));
    }
    private sealed class Context(string root):IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class Fixture(byte[] body):IPluginHttpGateway
    {
        public int Calls;public bool Fail;
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token){token.ThrowIfCancellationRequested();if(!SouthboundHistorySource.IsSource(uri))throw new InvalidDataException("来源未登记");Calls++;return Task.FromResult(new PublicHttpResponse(Fail?503:200,Fail?[]:body,"application/json"));}
    }
}
