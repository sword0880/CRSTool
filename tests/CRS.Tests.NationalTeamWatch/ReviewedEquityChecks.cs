using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>固定公开PDF的隔离登记；验证A/H隔离、缺失未知和金融事务，不向正式Worker写测试资料。</summary>
internal static class ReviewedEquityChecks
{
    private static void Check(bool ok,string text){if(!ok)throw new InvalidDataException(text);Console.WriteLine("PASS 已核A股子集："+text);}
    private static void Reject(Action action,string text)
    {try{action();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("未拒绝："+text);}
    private static byte[] Pdf(string directory,HistoricalEquityPdf doc)
    {
        var period=doc.ReportAsOf.Month==9?"q3":doc.Ticker=="601939"?"half-cninfo":"half";
        return File.ReadAllBytes(Path.Combine(directory,doc.Ticker,"research",$"{doc.Ticker}-{period}-fixed.pdf"));
    }
    public static void Run(string directory)
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.ReviewedEquity",Guid.NewGuid().ToString("N"));
        var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        using(var repo=TestMarketData.Open(data,raw))
        {
            HistoricalEquityChecks.CatalogContracts(Path.Combine(root,"legacy-catalog"));
            var docs=repo.HistoricalEquityDocuments().Where(x=>x.Scope=="ReviewedAShareDisclosedSubset").OrderBy(x=>x.ReportAsOf).ToArray();
            Check(docs.Length==18&&docs.Sum(x=>x.ReviewedAccounts)==39&&docs.Count(x=>x.ReviewedAccounts==0)==4,"18份原文、39个账户历史快照及4份未匹配披露分开");
            Check(docs.All(d=>ReviewedEquitySources.All.Any(s=>s.Id==d.Id&&s.Sha256==d.OriginalHash)),"金融摘录逐一绑定固定来源哈希");
            foreach(var source in ReviewedEquitySources.All)
            {
                var uri=new Uri(source.Url);
                Check(OfficialEquityDocuments.IsAllowed(uri)&&OfficialEquityDocuments.Matches(uri,source.Market,source.Ticker,source.ReportAsOf)
                    &&!OfficialEquityDocuments.Matches(uri,"SSE","000000",source.ReportAsOf)
                    &&!OfficialEquityDocuments.IsAllowed(new(source.Url+"?download=1"))
                    &&!OfficialEquityDocuments.IsAllowed(new(source.Url+"#other")),"固定原文精确身份："+source.Id);
            }
            Check(!OfficialEquityDocuments.IsAllowed(new("https://static.cninfo.com.cn/finalpage/2026-10-31/9999999999.PDF"))
                &&!OfficialEquityDocuments.IsAllowed(new("https://www.cninfo.com.cn/new/disclosure")),"巨潮不开放目录、其他文件或自动替换原件");
            var ccb=docs.Single(x=>x.Ticker=="601939"&&x.ReportAsOf.Month==6);var original=Pdf(directory,ccb);
            var synthetic=DirectEquityChecks.Source(new(2026,8,29),("汇金公司",142590494651,54.51m),("中国证券金融股份有限公司",2189259672,.84m));
            using var json=JsonDocument.Parse(synthetic);
            var body=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(synthetic).Replace("600036","601939"));
            repo.SaveEquityDisclosure("SSE","601939",OfficialEquitySource.Request("SSE","601939"),body);
            var candidate=repo.EquityDisclosures(new("SSE","601939")).Single();
            Reject(()=>repo.ReviewEquity(new(candidate.Id,ccb.ReportAsOf,ccb.Published,ccb.DocumentUrl,"PDF P72-74","隔离反例",false,"Unknown",Account:"汇金公司",OriginalConfirmed:true,Institution:"汇金投资"),original),"同一真PDF也不能把1425亿H股批准为A股");
            var changed=original.ToArray();changed[^1]^=1;
            Reject(()=>repo.ImportHistoricalEquityPdf(ccb.Id,changed,"隔离测试","改动原文",Guid.NewGuid()),"改变一个字节不能继承核验结论");
            Rollback(repo,data,ccb,original);
            foreach(var doc in docs)
            {
                var id=Guid.NewGuid();var bytes=Pdf(directory,doc);
                var receipt=repo.ImportHistoricalEquityPdf(doc.Id,bytes,"隔离测试","固定原文A股子集",id);
                Check(receipt.Outcome=="Imported"&&receipt.ReviewedAccounts==doc.ReviewedAccounts
                    &&repo.ImportHistoricalEquityPdf(doc.Id,bytes,"隔离测试","固定原文A股子集",id)==receipt,"事务及原ID幂等："+doc.Id);
            }
            Check(repo.EquityDisclosures(new("SSE","601939")).Single(x=>x.Id==candidate.Id).State=="NeedsReview","源日期及具名数量相同但原始哈希不同，不归档其他候选");
            var ccbEvents=repo.EquityEvents(new("SSE","601939"));
            Check(ccbEvents.Where(x=>x.ReportAsOf is not null).All(x=>x.Shares!=142590494651)
                &&ccbEvents.Any(x=>x.ReportAsOf==new DateOnly(2026,6,30)&&x.Account=="汇金公司"&&x.Shares==267392944),"正式历史保留真实A股拆分");
            Check(ccbEvents.Where(x=>x.Account=="中央汇金资产管理有限责任公司").All(x=>x.Percent is null),"未披露资管比例保持null");
            var hualu=repo.EquityHoldings("600426",null,null,false);
            Check(hualu.Any(x=>x.Account=="全国社保基金一零三组合"&&x.State=="NotListed"&&x.Shares is null&&x.Delta is null),"新报告未列示不补零、不计算清仓");
            Check(hualu.Where(EquityStockList.IsVerifiedHolding).All(x=>x.Nature=="Unknown"&&x.Delta is null),"转增后的名义股数不当买卖");
            var chip=repo.EquityHoldings("688012",null,null,false).Single();
            Check(chip.Institution=="国家大基金"&&chip.Account==InstitutionAccounts.ChipFund2ManagedAccount&&chip.Shares==28314016
                &&InstitutionAccounts.Group(chip.Account) is null,"复合原文账户仅人工绑定一次，管理人不另算持有人");
            Check(new[]{"600887","600036","600519","600900"}.All(t=>repo.EquityHoldings(t,null,null,false).Count==0),"无匹配账户的报告不生成零持仓");
        }
        using var reopened=TestMarketData.Open(data,raw);
        var verified=EquityStockList.Build(reopened.EquityHoldings(null,null,null,false),true);
        Check(verified.Count==9&&verified.Sum(x=>x.AccountCount)==24,"隔离重启回读9只、24个当前已核A股账户，未借用公牛既有数据");
        Check(verified.SelectMany(x=>x.Holders).All(x=>x.ReportAsOf==new DateOnly(2026,9,30)&&x.Nature=="Unknown"&&x.Delta is null),"最新报告期、未知性质及无交易差额跨重启保持");
    }
    private static void Rollback(MarketRepository repo,string data,HistoricalEquityPdf doc,byte[] bytes)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());db.Open();
        using var q=db.CreateCommand();q.CommandText="CREATE TRIGGER fail_subset_receipt BEFORE INSERT ON equity_historical_receipt BEGIN SELECT RAISE(ABORT,'fixture');END";q.ExecuteNonQuery();
        var id=Guid.NewGuid();try{repo.ImportHistoricalEquityPdf(doc.Id,bytes,"隔离测试","事务回滚",id);throw new Exception("故障未发生");}catch(SqliteException){}
        Check(repo.HistoricalEquityReceipt(id) is null&&repo.EquityDisclosures(new(doc.Market,doc.Ticker)).All(x=>x.State!="Accepted"),"回执写入故障回滚整个新披露事务");
        q.CommandText="DROP TRIGGER fail_subset_receipt";q.ExecuteNonQuery();
    }
}
