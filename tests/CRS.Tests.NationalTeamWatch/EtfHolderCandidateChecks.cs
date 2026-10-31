using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static partial class EtfHolderCandidateChecks
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
        var layout=new PdfPageContent(1,100,100,[new("甲",10,80,20),new("乙",10,60,20),new("9",70,80,80)],[]);
        Check(PdfTextLayout.Cell(layout,0,50,50,90)=="甲\n乙","公共坐标截取保留跨行账户，不串入相邻数字列");
        Reject(()=>PdfTextLayout.Cell(layout,50,0,50,90),"公共坐标截取拒绝倒置矩形");
        Reject(()=>PdfTextLayout.Cell(layout,0,101,50,90),"公共坐标截取拒绝越出页面");
        Reject(()=>PdfTextLayout.Cell(layout,0,double.NaN,50,90),"公共坐标截取拒绝非有限坐标");
        try{PdfTextLayout.Lines(layout.Letters,new(true));throw new Exception("忽略版式取消。");}catch(OperationCanceledException){Check(true,"公共版式读取取消不返回空字符串");}
        var efundUnknown=document with{Market="SZSE",Ticker="159915"};
        Check(EfundChiNextHolderPdf.Parse(new(efundUnknown,unknown)).State=="UnsupportedTemplate","159915未知原件保持未支持，不当作未披露或零持仓");
        Check(HarvestCsi300HolderPdf.Parse(new(document with{Market="SZSE",Ticker="159919"},unknown)).State=="UnsupportedTemplate","159919未知原件保持未支持，不截取半张表或补零");
        Check(China50HolderPdf.Parse(new(document with{Ticker="510050"},unknown)).State=="UnsupportedTemplate","510050未知原件保持未支持，不冒充已知中报未披露");
        await EtfHolderCandidateUiChecks.RunAsync();
        RepositoryBoundary(Path.Combine(root,"boundary"),unknown);
        await ClientContracts();
        await China50ClientContracts();
        if(sourceDirectory is not null)
        {
            if(File.Exists(Path.Combine(sourceDirectory,"510050-2024.pdf")))await China50Golden(root,sourceDirectory);
            else if(File.Exists(Path.Combine(sourceDirectory,"159919-2024.pdf")))await HarvestGolden(root,sourceDirectory);
            else if(File.Exists(Path.Combine(sourceDirectory,"159915-2024.pdf")))await EfundGolden(root,sourceDirectory);
            else await Golden(root,sourceDirectory);
        }
        Console.WriteLine($"ETF持有人候选检查通过：{passed}项。原件未写入正式数据库。");
    }
    private static async Task ClientContracts()
    {
        var control=new HolderControl();var name="CRS.EtfHolder.Contracts."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var server=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(name));var key=new EtfKey("SZSE","159915");var original=control.Report;var id=original.Document.Id;
            Check((await reader.EtfHolderCandidatesAsync(key,id,stop.Token)).Holders.Count==11,"真实客户端接受159915已知候选版本");
            control.Report=original with{State="NotDisclosed",Scope="AggregateOnlyNoNamedTable",PercentBasis=null,Holders=[]};
            Check((await reader.EtfHolderCandidatesAsync(key,id,stop.Token)).State=="NotDisclosed","真实客户端保留未披露状态，不将空表升级成零持仓");
            foreach(var (label,invalid) in new[]{("未知解析版本",original with{ParserVersion="future-parser"}),("未知披露状态",original with{State="UnknownSuccess"}),
                ("未披露却携带持仓行",original with{State="NotDisclosed",Scope="AggregateOnlyNoNamedTable",PercentBasis=null}),
                ("未披露状态继承其他解析器",original with{ParserVersion=EtfHolderParserVersions.Huatai300,State="NotDisclosed",Scope="AggregateOnlyNoNamedTable",PercentBasis=null,Holders=[]}),
                ("完整表缺行",original with{Holders=original.Holders.Take(10).ToArray()}),("候选行号重复",original with{Holders=original.Holders.Select(x=>x with{Rank=1}).ToArray()}),
                ("另一基金解析版本",original with{ParserVersion=EtfHolderParserVersions.Huatai300}),("报告期丢失",original with{ReportAsOf=null}),
                ("空持仓字段",original with{Holders=null!}),("空持仓行",original with{Holders=[null!,..original.Holders.Skip(1)]}),
                ("另一原件身份",original with{Document=original.Document with{Id=new('C',64)}})})
            {
                control.Report=invalid;
                try{await reader.EtfHolderCandidatesAsync(key,id,stop.Token);throw new Exception("接受损坏持有人合同："+label);}
                catch(InvalidDataException){Check(true,"客户端拒绝"+label);}
            }
            var harvest=original with{Document=original.Document with{Ticker="159919",Url=HarvestCsi300HolderPdf.Annual2025Url},ParserVersion=EtfHolderParserVersions.HarvestCsi300,PhysicalPage=66};
            control.Report=harvest;
            Check((await reader.EtfHolderCandidatesAsync(new("SZSE","159919"),id,stop.Token)).ParserVersion==EtfHolderParserVersions.HarvestCsi300,"真实客户端接受159919固定版式候选合同");
            foreach(var invalid in new[]{harvest with{Holders=harvest.Holders.Take(3).ToArray()},harvest with{State="NotDisclosed",Scope="AggregateOnlyNoNamedTable",PercentBasis=null,Holders=[]},harvest with{Document=harvest.Document with{Ticker="159915"}}})
            {
                control.Report=invalid;
                try{await reader.EtfHolderCandidatesAsync(new("SZSE","159919"),id,stop.Token);throw new Exception("接受不完整或串证券的159919合同。");}
                catch(InvalidDataException){Check(true,"159919合同拒绝半张表、错误未披露或串证券");}
            }
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class HolderControl:IBackgroundControl
    {
        internal EtfHolderCandidateReport Report=new(new(new('A',64),"SZSE","159915",EfundChiNextHolderPdf.Annual2025Url,new('B',64),"2026-10-28T00:00:00+08:00"),
            EtfHolderParserVersions.EfundChiNext,"NeedsReview","合成合同",new(2025,12,31),new(2026,3,31),64,"份","ListedFundUnits","ListedTopTenTableWithSeparateFeederRow",
            Enumerable.Range(1,11).Select(i=>new EtfHolderCandidate(i,"合成账户"+i,"合成账户"+i,1,1,"合成页面",null)).ToArray());
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command!="QueryPlugin")throw new Exception("候选读取不得提交写命令。");
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(query.Operation=="query-capabilities")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownQuery"));
            var blocks=CRS.Serialization.JsonDocumentBlocks.Encode(Report,token);var rows=blocks.Skip(query.Offset).Take(query.Count).ToArray();
            var next=query.Offset+rows.Length<blocks.Count?query.Offset+rows.Length:(int?)null;
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(
                new EtfQueryPage(JsonSerializer.Serialize(rows),new('D',32),next){TotalRows=blocks.Count})));
        }
    }
    private static void RepositoryBoundary(string root,byte[] bytes)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var data=Path.Combine(root,"data");
        using var repository=TestMarketData.Open(data,raw);const string url="https://www.sse.com.cn/unknown.pdf";
        repository.RegisterAlertDocument(new(Key.Market,Key.Ticker,url),bytes);var doc=repository.AlertDocuments(Key).Single();
        Check(((IMarketReadSessions)repository).Read(x=>Huatai300HolderPdf.Parse(x.EtfHolderDocument(Key,doc.Id)).State,default)=="UnsupportedTemplate",
            "真实只读仓储中的未支持模板保留明确状态");
        using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());db.Open();
        void Sql(string sql){using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        Sql("UPDATE source_fetch SET source_url='https://www.sse.com.cn/wrong.pdf'");
        Reject(()=>repository.EtfHolderDocument(Key,doc.Id),"来源URL与原件登记不一致不能解析");
        Sql("UPDATE source_fetch SET source_url='"+url+"'");
        Sql("INSERT INTO quality_issue SELECT 'holder-q15','Q15','fixture',fetch_id,'Critical','Open',fetch_id,NULL,NULL FROM source_fetch LIMIT 1");
        Reject(()=>repository.EtfHolderDocument(Key,doc.Id),"未解除Q15即便原件当前哈希正确仍阻断读取");
        Sql("DELETE FROM quality_issue WHERE id='holder-q15'");
        Sql("UPDATE source_fetch SET status='ParseFailed'");
        Reject(()=>repository.EtfHolderDocument(Key,doc.Id),"已失败来源不能升级为候选成功");
        Check(repository.NamedEtfHoldings(Key).Count==0,"边界检查不产生已审核持仓");
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
        await Endpoint(root,Key,last,reports[^1]);
        File.WriteAllText(Path.Combine(root,"holder-candidate-reports.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(root,"raw",last.Hash+".raw"),"damaged");
        Reject(()=>repository.EtfHolderDocument(Key,last.Id),"解析前原件损坏拒绝返回候选");
        Console.WriteLine("候选及隔离证据："+root);
    }
    private static async Task EfundGolden(string root,string directory)
    {
        var rawPath=Path.Combine(root,"efund","raw");var raw=new RawEvidenceStore(rawPath);
        var workspace=Path.Combine(root,"efund");var key=new EtfKey("SZSE","159915");
        using var repository=TestMarketData.Open(Path.Combine(workspace,"data"),raw);
        var reports=new List<EtfHolderCandidateReport>();
        using var expected=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"159915-holder-table-expected.json")));
        foreach(var (name,url,asOf) in new[]{("159915-2024",EfundChiNextHolderPdf.Annual2024Url,new DateOnly(2024,12,31)),
            ("159915-2025",EfundChiNextHolderPdf.Annual2025Url,new DateOnly(2025,12,31)),("159915-2026H1",EfundChiNextHolderPdf.Interim2026Url,new DateOnly(2026,6,30))})
        {
            var bytes=File.ReadAllBytes(Path.Combine(directory,name+".pdf"));
            using var receipt=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,name+"-receipt.json")));
            Check(receipt.RootElement.GetProperty("Sha256").GetString()==Hash(bytes)&&receipt.RootElement.GetProperty("TlsVerification").GetBoolean(),"官方原件与独立下载收据摘要一致 "+name);
            repository.RegisterAlertDocument(new(key.Market,key.Ticker,url),bytes);
            var registered=repository.AlertDocuments(key).Single(x=>x.Url==url);
            var frozen=((IMarketReadSessions)repository).Read(x=>x.EtfHolderDocument(key,registered.Id),default);
            var report=EfundChiNextHolderPdf.Parse(frozen);reports.Add(report);
            Check(report.ParserVersion==EtfHolderParserVersions.EfundChiNext&&report.ReportAsOf==asOf&&report.PublishedAt is null,"159915报告期与未知准确公布时刻保留 "+name);
            if(asOf.Month==12)
            {
                var annual=expected.RootElement.GetProperty(name);
                Check(report.State=="NeedsReview"&&report.Holders.Count==11&&report.Holders.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11)),"跨页或单页完整读取11名持有人 "+name);
                foreach(var row in annual.EnumerateArray())
                {
                    var actual=report.Holders.Single(x=>x.Rank==row.GetProperty("Rank").GetInt32());
                    Check(actual.RegisteredHolder==row.GetProperty("Name").GetString()&&actual.Shares==row.GetProperty("Shares").GetDecimal()
                        &&actual.PercentOfListedUnits==row.GetProperty("Percent").GetDecimal(),"逐行对照独立页面摘录 "+name+"/"+actual.Rank);
                }
                Check(report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed)&&report.Holders[^1].RawHolder.Contains('\n')
                    &&report.PercentBasis=="ListedFundUnits","候选、跨行原文和上市份额分母不自动升级 "+name);
                Check(report.Holders.Single(x=>x.RegisteredHolder=="香港中央结算有限公司").SuggestedInstitution is null,"名义结算账户不自动归国家队 "+name);
            }
            else Check(report is{State:"NotDisclosed",Scope:"AggregateOnlyNoNamedTable",Holders.Count:0},"仅汇总的中期报告明确未披露具名表，不冒充清仓");
            Reject(()=>EfundChiNextHolderPdf.Parse(frozen with{Document=registered with{Ticker="159919"}}),"真实原件不能串证券 "+name);
            Reject(()=>EfundChiNextHolderPdf.Parse(frozen with{Document=registered with{Url=url+"?v=1"}}),"真实原件不能继承变体路径 "+name);
            var beforePublication=report.PublicationDate!.Value.AddDays(-1).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)+"T23:59:59+08:00";
            Reject(()=>EfundChiNextHolderPdf.Parse(frozen with{Document=registered with{FirstSeenAt=beforePublication}}),"存储目录日期不能替代公告日期准入 "+name);
            await Endpoint(workspace,key,registered,report);
        }
        Check(repository.NamedEtfHoldings(key).Count==0&&repository.AlertEvents(new()).Count==0&&repository.AlertOutbox().Count==0,"真实159915候选不写入审核持仓、退出事件或通知");
        var last=reports[^1].Document;File.WriteAllText(Path.Combine(rawPath,last.Hash+".raw"),"damaged");
        Reject(()=>repository.EtfHolderDocument(key,last.Id),"未披露表报告也不能跳过原件即时完整性核验");
        File.WriteAllText(Path.Combine(root,"efund-holder-candidate-reports.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static async Task HarvestGolden(string root,string directory)
    {
        var workspace=Path.Combine(root,"harvest");var rawPath=Path.Combine(workspace,"raw");var raw=new RawEvidenceStore(rawPath);var key=new EtfKey("SZSE","159919");
        using var repository=TestMarketData.Open(Path.Combine(workspace,"data"),raw);
        using var expected=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","EtfHolders159919.json")));
        var reports=new List<EtfHolderCandidateReport>();
        foreach(var (year,url,publication) in new[]{(2024,HarvestCsi300HolderPdf.Annual2024Url,new DateOnly(2025,3,28)),(2025,HarvestCsi300HolderPdf.Annual2025Url,new DateOnly(2026,3,30))})
        {
            var label="159919-"+year;var bytes=File.ReadAllBytes(Path.Combine(directory,label+".pdf"));
            repository.RegisterAlertDocument(new(key.Market,key.Ticker,url),bytes);
            var registered=repository.AlertDocuments(key).Single(x=>x.Url==url);
            var frozen=((IMarketReadSessions)repository).Read(x=>x.EtfHolderDocument(key,registered.Id),default);
            var report=HarvestCsi300HolderPdf.Parse(frozen);reports.Add(report);
            Check(report is{State:"NeedsReview",Holders.Count:11,PublishedAt:null}&&report.ReportAsOf==new DateOnly(year,12,31)&&report.PublicationDate==publication,"159919完整跨页报告、期末和日期精度 "+year);
            foreach(var row in expected.RootElement.GetProperty(label).EnumerateArray())
            {
                var actual=report.Holders.Single(x=>x.Rank==row.GetProperty("Rank").GetInt32());
                Check(actual.RegisteredHolder==row.GetProperty("Name").GetString()&&actual.Shares==row.GetProperty("Shares").GetDecimal()&&actual.PercentOfListedUnits==row.GetProperty("Percent").GetDecimal(),"159919逐行对照独立视觉摘录 "+year+"/"+actual.Rank);
            }
            Check(report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed)&&report.Holders[^1].RegisteredHolder.EndsWith("联接基金(LOF)",StringComparison.Ordinal)&&report.PercentBasis=="ListedFundUnits","159919保留另列联接基金、场内分母和待审状态 "+year);
            Check(report.Holders[0].SuggestedInstitution=="汇金投资"&&report.Holders[1].SuggestedInstitution=="汇金资管","159919分别标识两类汇金登记法人 "+year);
            if(year==2024)Check(report.Holders[^1].EvidencePage.Contains("62至63")&&report.Holders[^1].RawHolder.Contains("\n"),"2024跨页账户名称完整拼接且证据注明两页");
            else Check(report.Holders[2].EvidencePage.Contains("66页")&&report.Holders[3].EvidencePage.Contains("67页")&&report.Holders[^1].EvidencePage.Contains("67页"),"2025续表第4至11行保留实际物理页");
            Reject(()=>HarvestCsi300HolderPdf.Parse(frozen with{Document=registered with{Ticker="159915"}}),"159919原件不能串证券 "+year);
            Reject(()=>HarvestCsi300HolderPdf.Parse(frozen with{Document=registered with{Url=url+"?v=1"}}),"159919原件不能继承变体路径 "+year);
            Reject(()=>HarvestCsi300HolderPdf.Parse(frozen with{Document=registered with{FirstSeenAt=publication.AddDays(-1).ToString("yyyy-MM-dd")+"T23:59:59+08:00"}}),"159919附件存储日不能替代公告日期 "+year);
            var damaged=bytes.ToArray();damaged[^1]^=1;
            Reject(()=>HarvestCsi300HolderPdf.Parse(frozen with{Content=damaged}),"159919原件字节变化拒绝沿用模板 "+year);
            try{HarvestCsi300HolderPdf.Parse(frozen,new(true));throw new Exception("取消返回了候选。");}catch(OperationCanceledException){Check(true,"159919取消不返回半张表 "+year);}
            await Endpoint(workspace,key,registered,report);
        }
        Check(repository.NamedEtfHoldings(key).Count==0&&repository.AlertEvents(new()).Count==0&&repository.AlertOutbox().Count==0,"159919解析和后台读均不审核真实持仓或新增退出通知");
        File.WriteAllText(Path.Combine(root,"harvest-holder-candidate-reports.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("159919黄金原件隔离报告："+root);
    }
    private static async Task Endpoint(string root,EtfKey key,AlertDocument document,EtfHolderCandidateReport expected)
    {
        await using var session=new CRS.Plugins.NationalTeam.Jobs.NationalTeamBackgroundSession(new Context(root),new NoHttp());
        await session.StartAsync(default);
        var query=new EtfRequest("etf-holder-candidates",key.Market,key.Ticker,Revision:document.Id,Count:1);
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
        Check(JsonSerializer.Serialize(report)==JsonSerializer.Serialize(expected),"真实后台分块完整传回候选、披露状态和原件身份，不联网");
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
