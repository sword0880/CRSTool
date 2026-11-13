using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static partial class EtfHolderCandidateChecks
{
    /// <summary>默认回归经过真实本机管道，拒绝版本串证券及把中报未披露转换成零持仓。</summary>
    private static async Task China50ClientContracts()
    {
        var control=new HolderControl();var original=control.Report;
        var annual=original with{Document=original.Document with{Market="SSE",Ticker="510050",Url=China50HolderPdf.Annual2025Url},ParserVersion=EtfHolderParserVersions.China50,PhysicalPage=53};
        control.Report=annual;var pipe="CRS.China50.Contracts."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
        try
        {
            var reader=new WorkerEtfWorkspace(new(pipe));var key=new EtfKey("SSE","510050");var id=annual.Document.Id;
            Check((await reader.EtfHolderCandidatesAsync(key,id,stop.Token)).Holders.Count==11,"510050客户端接受完整已知年报候选版本");
            var interim=annual with{Document=annual.Document with{Url=China50HolderPdf.Interim2026Url},State="NotDisclosed",ReportAsOf=new(2026,6,30),PublicationDate=new(2026,8,31),PhysicalPage=48,Scope="AggregateOnlyNoNamedTable",PercentBasis=null,Holders=[]};
            control.Report=interim;
            Check((await reader.EtfHolderCandidatesAsync(key,id,stop.Token)).State=="NotDisclosed","510050客户端保留中报未披露，不回退旧年报候选");
            foreach(var invalid in new[]{annual with{Holders=annual.Holders.Take(10).ToArray()},annual with{Document=annual.Document with{Ticker="510300"}},
                interim with{Holders=annual.Holders},interim with{ParserVersion=EtfHolderParserVersions.Huatai300},interim with{PercentBasis="ListedFundUnits"}})
            {
                control.Report=invalid;
                try{await reader.EtfHolderCandidatesAsync(key,id,stop.Token);throw new Exception("接受损坏的510050候选合同。");}
                catch(InvalidDataException){Check(true,"510050客户端拒绝缺行、串证券及伪造未披露合同");}
            }
            control.Report=annual with{Document=annual.Document with{Market="SZSE",Ticker="159915"}};
            try{await reader.EtfHolderCandidatesAsync(new("SZSE","159915"),id,stop.Token);throw new Exception("510050解析版本串入另一证券。");}
            catch(InvalidDataException){Check(true,"510050解析版本不能继承到另一只ETF");}
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }

    /// <summary>独立页面摘录对照每个账户和数字；真实后台只读链路不得生成审核事实、退出事件或通知。</summary>
    private static async Task China50Golden(string root,string directory)
    {
        var workspace=Path.Combine(root,"china50");var rawPath=Path.Combine(workspace,"raw");var key=new EtfKey("SSE","510050");
        using var repository=TestMarketData.Open(Path.Combine(workspace,"data"),new RawEvidenceStore(rawPath));
        using var expected=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","EtfHolders510050.json")));
        var reports=new List<EtfHolderCandidateReport>();
        foreach(var (label,url,asOf,publication) in new[]{("510050-2024",China50HolderPdf.Annual2024Url,new DateOnly(2024,12,31),new DateOnly(2025,3,31)),
            ("510050-2025",China50HolderPdf.Annual2025Url,new DateOnly(2025,12,31),new DateOnly(2026,3,31)),
            ("510050-2026-interim",China50HolderPdf.Interim2026Url,new DateOnly(2026,6,30),new DateOnly(2026,8,31))})
        {
            var bytes=File.ReadAllBytes(Path.Combine(directory,label+".pdf"));
            repository.RegisterAlertDocument(new(key.Market,key.Ticker,url),bytes);
            var document=repository.AlertDocuments(key).Single(x=>x.Url==url);
            var frozen=((IMarketReadSessions)repository).Read(x=>x.EtfHolderDocument(key,document.Id),default);
            var report=China50HolderPdf.Parse(frozen);reports.Add(report);
            Check(report.ParserVersion==EtfHolderParserVersions.China50&&report.ReportAsOf==asOf&&report.PublicationDate==publication&&report.PublishedAt is null,"510050报告期及公告日期精度保留 "+label);
            if(asOf.Month==12)
            {
                Check(report is{State:"NeedsReview",Holders.Count:11}&&report.Holders.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11)),"510050年报续表完整11行 "+label);
                foreach(var row in expected.RootElement.GetProperty(label).EnumerateArray())
                {
                    var actual=report.Holders.Single(x=>x.Rank==row.GetProperty("Rank").GetInt32());
                    Check(actual.RegisteredHolder==row.GetProperty("Name").GetString()&&actual.Shares==row.GetProperty("Shares").GetDecimal()&&actual.PercentOfListedUnits==row.GetProperty("Percent").GetDecimal(),"510050逐行对照独立视觉摘录 "+label+"/"+actual.Rank);
                }
                Check(report.Holders[0].SuggestedInstitution=="汇金投资"&&report.Holders[1].SuggestedInstitution=="汇金资管"&&report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed),"510050只提示主体分类，不能自动确认账户或独立所有权 "+label);
                Check(report.Holders[^1].RawHolder.Contains('\n')&&report.PercentBasis=="ListedFundUnits"&&report.Unit=="份","510050联接基金换行和上市份额分母保留 "+label);
                if(asOf.Year==2024)Check(report.Holders[7].EvidencePage.Contains("55页")&&report.Holders[8].EvidencePage.Contains("56页"),"510050跨页续表保留实际物理页而非印刷页码");
            }
            else Check(report is{State:"NotDisclosed",Holders.Count:0,Scope:"AggregateOnlyNoNamedTable",PercentBasis:null},"510050中报仅汇总不补造具名零持仓或清仓");
            Reject(()=>China50HolderPdf.Parse(frozen with{Document=document with{Ticker="510300"}}),"510050原件不能串证券 "+label);
            Reject(()=>China50HolderPdf.Parse(frozen with{Document=document with{Url=url+"?v=1"}}),"510050原件不能继承变体路径 "+label);
            Reject(()=>China50HolderPdf.Parse(frozen with{Document=document with{FirstSeenAt=publication.AddDays(-1).ToString("yyyy-MM-dd")+"T23:59:59+08:00"}}),"510050首见早于公告拒绝 "+label);
            var changed=bytes.ToArray();changed[^1]^=1;
            Reject(()=>China50HolderPdf.Parse(frozen with{Content=changed}),"510050损坏字节不能沿用已核模板 "+label);
            Check(China50HolderPdf.Parse(new(document with{Hash=Hash(changed)},changed)).State=="UnsupportedTemplate","510050同路径新字节必须重新核对模板 "+label);
            try{China50HolderPdf.Parse(frozen,new(true));throw new Exception("取消返回了候选。");}catch(OperationCanceledException){Check(true,"510050取消不返回半张表 "+label);}
            await Endpoint(workspace,key,document,report);
        }
        Check(repository.NamedEtfHoldings(key).Count==0&&repository.AlertEvents(new()).Count==0&&repository.AlertOutbox().Count==0,"510050候选不写入已审持仓、退出事件或通知");
        var last=reports[^1].Document;File.WriteAllText(Path.Combine(rawPath,last.Hash+".raw"),"damaged");
        Reject(()=>repository.EtfHolderDocument(key,last.Id),"510050未披露也必须通过原件即时完整性核验");
        File.WriteAllText(Path.Combine(root,"china50-holder-candidate-reports.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("510050黄金原件隔离报告："+root);
    }
}
