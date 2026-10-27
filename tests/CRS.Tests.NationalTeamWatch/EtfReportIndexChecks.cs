using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Serialization;
using Microsoft.Data.Sqlite;

internal static class EtfReportIndexChecks
{
    private static readonly EtfKey SseKey=new("SSE","510300"),SzseKey=new("SZSE","159915");
    private static readonly DateOnly From=new(2026,1,1),To=new(2026,10,27);
    private static int passed;
    internal static byte[] SsePage(int total=1,int page=1)
    {
        var rows=Enumerable.Range((page-1)*25,Math.Min(25,Math.Max(0,total-(page-1)*25))).Select(i=>new{SECURITY_CODE="510300",TITLE="合成2026年中期报告",SSEDATE="2026-08-31",ORG_BULLETIN_TYPE_DESC="中期报告",BULLETIN_TYPE_DESC="定期报告(基金)",NUM=(i+1).ToString(CultureInfo.InvariantCulture),URL=$"/disclosure/fund/announcement/c/new/2026-08-31/510300_20260831_T{i}.pdf"}).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new{sqlId=OfficialEtfReportIndex.SseSqlId,type="inParams",isPagination="true",actionErrors=Array.Empty<string>(),actionMessages=Array.Empty<string>(),fieldErrors=new{},
            pageHelp=new{total,pageCount=(total+24)/25,pageNo=page,pageSize=25,data=rows},result=rows});
    }
    internal static byte[] SzsePage(int total=1,int page=1)=>JsonSerializer.SerializeToUtf8Bytes(new{announceCount=total,data=Enumerable.Range((page-1)*50,Math.Min(50,Math.Max(0,total-(page-1)*50))).Select(i=>new{
        id=$"{i+1:x8}-0000-0000-0000-000000000001",annId=i+1,title="合成2026年中期报告",publishTime="2026-08-31 00:00:00",attachFormat="PDF",secCode=new[]{"159915"},attachPath=$"/disc/disk03/finalpage/2026-08-31/{i+1:x8}-1111-0000-0000-000000000001.PDF"}).ToArray()});
    internal static async Task RunAsync(string root,string? originals=null)
    {
        passed=0;originals??=Path.Combine(AppContext.BaseDirectory,"Fixtures","EtfReports");Directory.CreateDirectory(root);var sse=SsePage();var szse=SzsePage();
        Check(OfficialEtfReportIndex.Parse(SseKey,From,To,1,sse).Documents.Single().DocumentUrl.Contains("/disclosure/fund/"),"沪市使用基金平面目录，不复用股票公告分组");
        var deep=OfficialEtfReportIndex.Parse(SzseKey,From,To,1,szse).Documents.Single();
        Check(deep.DocumentUrl.StartsWith("https://disc.static.szse.cn/download/disc/",StringComparison.Ordinal)&&deep.OriginalPublication.EndsWith("00:00:00",StringComparison.Ordinal),"深市保留原始时间文字，不推造准确公布时点");
        foreach(var key in new[]{SseKey,SzseKey})Reject(()=>OfficialEtfReportIndex.Request(key,From,To.AddYears(4),1),"过大日期区间拒绝"+key.Market);
        Reject(()=>OfficialEtfReportIndex.Parse(SseKey,From,To,1,Change(sse,"510300","510050")),"沪市证券错配拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SzseKey,From,To,1,Change(szse,"159915","159919")),"深市证券错配拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SseKey,From,To,2,sse),"错页码拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SzseKey,From,To,1,Change(szse,"/disc/","https://evil.example/disc/")),"未知PDF来源拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SseKey,From,To,1,Change(sse,"COMMON_PL_JJXX_JJGG_NEW_L","other")),"错误接口响应拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SseKey,From,To,1,Change(sse,"20260831_T","20260830_T")),"路径日期错配拒绝");
        Reject(()=>OfficialEtfReportIndex.Parse(SzseKey,From,To,1,Encoding.UTF8.GetBytes("{\"announceCount\":0,\"announceCount\":1,\"data\":[]}")),"重复JSON字段拒绝");
        Reject(()=>OfficialEtfReportIndex.ParseCapture(SseKey,From,To,[SsePage(26)]),"不提交缺少尾页的目录");
        Reject(()=>OfficialEtfReportIndex.ParseCapture(SseKey,From,To,[SsePage(26),Change(SsePage(26,2),"_T25","_T0")]),"跨页重复报告拒绝");
        Check(OfficialEtfReportIndex.ParseCapture(SseKey,From,To,[SsePage(26),SsePage(26,2)]).Sum(x=>x.Documents.Count)==26,"跨页完整响应保留全部行");
        try{OfficialEtfReportIndex.Parse(SseKey,From,To,1,sse,new(true));throw new Exception("忽略取消");}catch(OperationCanceledException){Check(true,"来源解析响应取消");}
        var culture=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=new("th-TH");Check(OfficialEtfReportIndex.Request(SseKey,From,To,1).Query.Contains("START_DATE=2026-01-01")&&Encoding.UTF8.GetString(OfficialEtfReportIndex.RequestBody(SzseKey,From,To,1)!).Contains("2026-01-01"),"请求日期不受系统历法影响");}
        finally{CultureInfo.CurrentCulture=culture;}
        OfficialEtfReportIndex.ValidatePost(OfficialEtfReportIndex.SzseEndpoint,OfficialEtfReportIndex.RequestBody(SzseKey,From,To,1)!);
        Reject(()=>OfficialEtfReportIndex.ValidatePost(new("https://www.szse.cn/api/other"),OfficialEtfReportIndex.RequestBody(SzseKey,From,To,1)!),"JSON查询不允许其他POST地址");
        Reject(()=>OfficialEtfReportIndex.ValidatePost(OfficialEtfReportIndex.SzseEndpoint,Change(OfficialEtfReportIndex.RequestBody(SzseKey,From,To,1)!,"50","500")),"JSON查询不允许放宽分页");
        var rawDirectory=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawDirectory);using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw);
        Check(repo.EtfReportIndex(SseKey) is{State:"NotSearched",DeclaredTotal:null},"没有检索记录保持未知，不伪造成零条");
        var command=Guid.NewGuid();repo.SaveEtfReportIndex(SseKey,From,To,[sse],command);var before=repo.EtfReportIndex(SseKey);
        repo.SaveEtfReportIndex(SseKey,From,To,[],command);
        Check(repo.EtfReportCommandComplete(command,SseKey,From,To)&&repo.EtfReportIndex(SseKey).SearchId==before.SearchId,"已提交命令重放幂等");
        Reject(()=>repo.SaveEtfReportIndex(SzseKey,From,To,[szse],command),"同命令不能改证券后继承已完成回执");
        Reject(()=>repo.SaveEtfReportIndex(SseKey,From,To,[SsePage(26)]),"尾页失败不覆盖旧目录");
        Check(repo.EtfReportIndex(SseKey).SearchId==before.SearchId,"失败保留旧检索身份");
        repo.SaveEtfReportIndex(SzseKey,From,To,[szse]);
        Check(repo.Read(r=>r.EtfReportIndex(SzseKey),default).State=="IndexedUnreviewed","正式独立只读快照返回未核验目录");
        var hash=raw.Save(sse);File.WriteAllText(Path.Combine(rawDirectory,hash+".raw"),"damaged");
        Check(repo.EtfReportIndex(SseKey) is{State:"EvidenceUnavailable",Documents.Count:1},"未巡检的原件损坏也立即撤去有效状态并保留线索");repo.ReconcileEvidenceIntegrity();
        Check(repo.EtfReportIndex(SseKey) is{State:"EvidenceUnavailable",Documents.Count:1},"原件损坏保留历史线索并撤去有效来源状态");
        File.WriteAllBytes(Path.Combine(rawDirectory,hash+".raw"),sse);
        Check(repo.EtfReportIndex(SseKey).State=="EvidenceUnavailable","字节恢复但Q15仍Open时不放行");
        repo.ReconcileEvidenceIntegrity();Check(repo.EtfReportIndex(SseKey).State=="IndexedUnreviewed","解除隔离只恢复未核验目录，不批准持仓");
        repo.SaveEtfReportIndex(SseKey,From,To,[SsePage(0)]);
        Check(repo.EtfReportIndex(SseKey) is{State:"IndexedUnreviewed",DeclaredTotal:0,Documents.Count:0},"真实成功的零目录有独立查询证据，不代表机构零持仓");
        Check(repo.NamedEtfHoldings(SseKey).Count==0&&repo.AlertMetadata().Count==0,"报告发现不写人工批准持仓或可比性");
        if(originals is not null)
        {
            foreach(var (key,file) in new[]{(SseKey,"sse-510300-periodic-2026-direct.json"),(SzseKey,"szse-159915-periodic-2026-page1.json")})
            {
                var bytes=File.ReadAllBytes(Path.Combine(originals,file));
                using var receipt=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(originals,Path.GetFileNameWithoutExtension(file)+".receipt.json")));
                Check(receipt.RootElement.GetProperty("DefaultTlsValidation").GetBoolean()&&receipt.RootElement.GetProperty("SourceProtocolReviewOnly").GetBoolean()
                    &&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))==receipt.RootElement.GetProperty("Sha256").GetString(),"保存响应与独立来源收据哈希绑定 "+key.Market);
                var parsed=OfficialEtfReportIndex.Parse(key,From,To,1,bytes);repo.SaveEtfReportIndex(key,From,To,[bytes]);
                Check(parsed.Total==6&&parsed.Documents.Count==6&&repo.EtfReportIndex(key).State=="IndexedUnreviewed","真实已保存官方响应6条目录逐字段接入"+key.Market);
            }
        }
        await Jobs(Path.Combine(root,"jobs"));
        EtfReportStorageChecks.Run(Path.Combine(root,"storage-boundaries"));
        await EtfReportGatewayChecks.RunAsync();
        await EtfReportIndexUiChecks.RunAsync();
        Console.WriteLine($"ETF目录验证通过：{passed}项；合成库/保存响应，不代表正式Worker或机构持仓验收。");
    }
    private static async Task Jobs(string root)
    {
        var gateway=new Gateway();await using var session=new NationalTeamBackgroundSession(new Context(root),gateway);await session.StartAsync(default);
        foreach(var key in new[]{SseKey,SzseKey})
        {
            var id=Guid.NewGuid();var payload=JsonSerializer.Serialize(new EtfRequest("etf-report-search",key.Market,key.Ticker,From,To));
            var result=await session.ExecuteAsync("etf-report-search",payload,default,id);
            Check(result.Status=="Succeeded","真实后台入口执行两市目录"+key.Market);
            var count=gateway.Calls;await session.ExecuteAsync("etf-report-search",payload,default,id);Check(gateway.Calls==count,"已提交命令不再次联网"+key.Market);
            var query=new EtfRequest("etf-report-index",key.Market,key.Ticker,Count:1);var blocks=new List<JsonDocumentBlock>();
            while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(query),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;query=query with{Snapshot=page.Snapshot,Offset=next};}
            var snapshot=JsonDocumentBlocks.Decode<EtfReportIndexSnapshot>(blocks,default);
            Check(snapshot.Key==key&&snapshot.State=="IndexedUnreviewed"&&snapshot.Documents.Count==1&&!snapshot.HoldingsConfirmed,"完整只读分块绑定证券且不批准金融事实"+key.Market);
        }
        gateway.Status=503;var failed=await session.ExecuteAsync("etf-report-search",JsonSerializer.Serialize(new EtfRequest("etf-report-search","SSE","510300",From,To)),default,Guid.NewGuid());
        Check(failed.Status=="RetryDeferred","来源失败明确延期，不当作零报告或完成");
    }
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class Gateway:IPluginHttpGateway,IPluginJsonHttpGateway
    {
        public int Calls,Status=200;
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token){Calls++;if(!OfficialEtfReportIndex.IsSseRequest(uri))throw new Exception("测试禁止其他HTTP");return Task.FromResult(new PublicHttpResponse(Status,SsePage(),"application/json"));}
        public Task<PublicHttpResponse> PostJsonAsync(Uri uri,byte[] bytes,CancellationToken token){Calls++;OfficialEtfReportIndex.ValidatePost(uri,bytes);return Task.FromResult(new PublicHttpResponse(Status,SzsePage(),"application/json"));}
    }
    private static byte[] Change(byte[] bytes,string from,string to)=>Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(from,to,StringComparison.Ordinal));
    private static void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS ETF目录："+text);}
    private static void Reject(Action work,string text){try{work();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("未拒绝："+text);}
}
