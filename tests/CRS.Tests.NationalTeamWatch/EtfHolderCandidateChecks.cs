using System.Security.Cryptography;
using System.Text.Json;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class EtfHolderCandidateChecks
{
    private static readonly EtfKey Key=new("SSE","510300");
    private static int passed;
    public static async Task RunAsync(string root,string? sourceDirectory=null)
    {
        Directory.CreateDirectory(root);
        var unknown="%PDF-unknown-template"u8.ToArray();var document=new AlertDocument(new('A',64),Key.Market,Key.Ticker,"https://www.sse.com.cn/unknown.pdf",Hash(unknown),DateTimeOffset.UtcNow.ToString("O"));
        var report=Huatai300HolderPdf.Parse(new(document,unknown));
        Check(report is{State:"UnsupportedTemplate",PublishedAt:null,PublicationDate:null}&&report.Holders.Count==0,"未知模板明确不支持，不制造零持仓或人工确认");
        Reject(()=>Huatai300HolderPdf.Parse(new(document with{Hash=new('B',64)},unknown)),"原件摘要不符拒绝解析");
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();try{Huatai300HolderPdf.Parse(new(document,unknown),cancel.Token);throw new Exception("忽略取消");}
            catch(OperationCanceledException){Check(true,"解析取消不返回空成功");}
        }
        Reject(()=>PdfReader.ReadSelected(unknown,[1,1]),"公共PDF读取拒绝重复页");
        Reject(()=>PdfReader.ReadSelected(unknown,[0]),"公共PDF读取拒绝非法页码");
        Reject(()=>PdfReader.ReadSelected(unknown,Enumerable.Range(1,33).ToArray()),"公共PDF选择页保持32页上限");
        await EtfHolderCandidateUiChecks.RunAsync();
        if(sourceDirectory is not null)await Golden(root,sourceDirectory);
        Console.WriteLine($"ETF持有人候选检查通过：{passed}项。原件未写入正式数据库。");
    }
    private static async Task Golden(string root,string directory)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var data=Path.Combine(root,"data");
        using var repository=TestMarketData.Open(data,raw);
        var reports=new List<EtfHolderCandidateReport>();
        foreach(var year in new[]{2024,2025})
        {
            var bytes=File.ReadAllBytes(Path.Combine(directory,$"510300-{year}.pdf"));
            var url=year==2025?"https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-03-31/510300_20260331_W6CZ.pdf"
                :"https://www.sse.com.cn/disclosure/fund/announcement/c/new/2025-03-31/510300_20250331_07Z1.pdf";
            repository.RegisterAlertDocument(new(Key.Market,Key.Ticker,url),bytes);
            var registered=repository.AlertDocuments(Key).Single(x=>x.Url==url);
            var frozen=((IMarketReadSessions)repository).Read(x=>x.EtfHolderDocument(Key,registered.Id),default);
            var report=Huatai300HolderPdf.Parse(frozen);reports.Add(report);
            Check(report.State=="NeedsReview"&&report.Holders.Count==11&&report.Holders.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11)),year+"年完整11行，不截断联接基金");
            Check(report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed)&&report.PublishedAt is null
                &&report.PublicationDate==new DateOnly(year+1,3,31)&&report.ReportAsOf==new DateOnly(year,12,31),"候选时序、日精度和未确认标记保留"+year);
            Check(report.Holders.Any(x=>x.RawHolder.Contains('\n'))&&report.PercentBasis=="ListedFundUnits"&&report.Unit=="份","跨行账户与上市总份额分母独立保留"+year);
            var expected=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"510300-holder-table-candidates.json"))).RootElement.EnumerateArray().Single(x=>x.GetProperty("ReportAsOf").GetString()==$"{year}-12-31");
            foreach(var row in expected.GetProperty("Rows").EnumerateArray())
            {
                var actual=report.Holders.Single(x=>x.Rank==row.GetProperty("Rank").GetInt32());
                Check(actual.RegisteredHolder==row.GetProperty("RegisteredHolder").GetString()
                    &&actual.Shares==decimal.Parse(row.GetProperty("Shares").GetString()!,System.Globalization.CultureInfo.InvariantCulture)
                    &&actual.PercentOfListedUnits==decimal.Parse(row.GetProperty("PercentOfListedUnits").GetString()!,System.Globalization.CultureInfo.InvariantCulture),$"逐行对照独立页面摘录{year}/{actual.Rank}");
            }
            Reject(()=>Huatai300HolderPdf.Parse(frozen with{Document=registered with{Ticker="510050"}}),"同原件不能串证券"+year);
            Reject(()=>Huatai300HolderPdf.Parse(frozen with{Document=registered with{Url=url+"?v=1"}}),"原件模板不能继承到变体路径"+year);
        }
        Check(reports[0].Holders.Single(x=>x.RegisteredHolder=="中央汇金资产管理有限责任公司").Shares==26621328344m
            &&reports[1].Holders.Single(x=>x.RegisteredHolder=="中央汇金资产管理有限责任公司").Shares==37858474974m
            &&reports.All(x=>x.Holders.Single(y=>y.RegisteredHolder=="中央汇金投资有限责任公司").Shares==35654598859m),"两汇金法人和四个独立核对数值保持分开");
        Check(repository.NamedEtfHoldings(Key).Count==0,"解析真实原件不写入已审核具名持仓");
        var last=reports[^1].Document;
        Reject(()=>repository.EtfHolderDocument(new("SZSE","159919"),last.Id),"仓储按证券绑定原件");
        await Endpoint(root,last,reports[^1]);
        File.WriteAllText(Path.Combine(root,"holder-candidate-reports.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(root,"raw",last.Hash+".raw"),"damaged");
        Reject(()=>repository.EtfHolderDocument(Key,last.Id),"解析前原件损坏拒绝返回候选");
        Console.WriteLine("候选及隔离证据："+root);
    }
    private static async Task Endpoint(string root,AlertDocument document,EtfHolderCandidateReport expected)
    {
        await using var session=new CRS.Plugins.NationalTeam.Jobs.NationalTeamBackgroundSession(new Context(root),new NoHttp());
        await session.StartAsync(default);
        var query=new EtfRequest("etf-holder-candidates",Key.Market,Key.Ticker,Revision:document.Id,Count:1);
        var blocks=new List<CRS.Serialization.JsonDocumentBlock>();var pages=0;
        Check(session.QueryOperation(JsonSerializer.Serialize(query))==query.Operation,"候选查询接入真实后台只读白名单");
        while(true)
        {
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(query),default));
            blocks.AddRange(JsonSerializer.Deserialize<List<CRS.Serialization.JsonDocumentBlock>>(page.Items)!);pages++;
            if(page.NextOffset is not {} next)break;
            query=query with{Snapshot=page.Snapshot,Offset=next};
        }
        var report=CRS.Serialization.JsonDocumentBlocks.Decode<EtfHolderCandidateReport>(blocks,default);
        Check(JsonSerializer.Serialize(report)==JsonSerializer.Serialize(expected),"真实后台分块完整传回11行和原件身份，不联网");
        Check(pages==blocks.Count&&blocks.Count==CRS.Serialization.JsonDocumentBlocks.Encode(expected,default).Count,
            "按实际冻结文档长度取得全部块，不要求短报告人为扩成多页");
    }
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoHttp:CRS.Plugin.Abstractions.IPluginHttpGateway
    {public Task<CRS.Plugin.Abstractions.PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("候选读取不能联网");}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS ETF持有人："+message);}
    private static void Reject(Action work,string message){try{work();}catch(InvalidDataException){Check(true,message);return;}throw new Exception("未拒绝："+message);}
}
