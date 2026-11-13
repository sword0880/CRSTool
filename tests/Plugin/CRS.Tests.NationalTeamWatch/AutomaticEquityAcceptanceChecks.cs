using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class AutomaticEquityAcceptanceChecks
{
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 自动持股："+label);}
    private static void Reject(Action action,string label)
    {try{action();}catch(Exception e) when(e is InvalidDataException or JsonException or KeyNotFoundException){Check(true,label);return;}throw new Exception("未拒绝："+label);}
    public static void Run(string directory)
    {
        var root=Path.GetFullPath(directory);var research=Path.Combine(Path.GetDirectoryName(root)!,"equity-remediation-20261104");
        var original=File.ReadAllBytes(Path.Combine(root,"cninfo-stocks.json"));var stocks=AutomaticEquitySource.ParseSecurities(original);
        Check(stocks.Length>5000&&stocks.Any(x=>x.Market=="SZSE")&&stocks.Any(x=>x.Market=="SSE"),"正式证券目录含沪深A股，不限14只观察股票");
        Check(!AutomaticEquityBasis.ConfirmedA("Unknown",100,90)&&!AutomaticEquityBasis.ConfirmedA("H股",100,100)
            &&AutomaticEquityBasis.ConfirmedA("Unknown",100,100)&&AutomaticEquityBasis.ConfirmedA("A股",100,90),"无限售不足总数或H股不当作总A股；明确A股采用期末总数");
        var source=ReviewedEquitySources.All.Single(x=>x.Ticker=="688012"&&x.ReportAsOf.Month==9);
        var stock=stocks.Single(x=>x.Ticker==source.Ticker);var pdf=File.ReadAllBytes(Path.Combine(research,"688012/research/688012-q3-fixed.pdf"));
        var doc=new AutomaticEquityDocument(Path.GetFileNameWithoutExtension(new Uri(source.Url).AbsolutePath),stock.Market,stock.Ticker,stock.Name,stock.OrgId,"2026年第三季度报告",source.Published,source.ReportAsOf,source.Url);
        var proof=AutomaticEquityPdf.Parse(doc,pdf);
        Check(proof.Accounts.Count==1&&proof.Holders.Single().Shares==28314016&&proof.Holders.Single().Percent is null,"真实大基金复合账户按总A股股数核验，不补比例");
        Reject(()=>AutomaticEquityPdf.Parse(doc with{Ticker="600426"},pdf),"证券不符不能自动批准");
        Reject(()=>AutomaticEquityPdf.Parse(doc with{ReportAsOf=new(2026,6,30)},pdf),"报告期不符不能自动批准");
        Reject(()=>AutomaticEquityPdf.Parse(doc with{Ticker="603195"},pdf),"公牛固定来源限制不放宽");
        var known=stocks.ToDictionary(x=>x.Ticker,StringComparer.Ordinal);
        var page=File.ReadAllBytes(Path.Combine(root,"global-20261031-page1.json"));
        var parsed=AutomaticEquitySource.ParsePage(new(2026,10,31),1,page,known);
        Check(parsed.Total==637&&parsed.OriginalRows==30&&parsed.Documents.Count>0,"真实官方单日目录的数量及附件身份完整核对");
        var bad=JsonNode.Parse(page)!;bad["announcements"]![0]!["orgId"]="wrong";
        Reject(()=>AutomaticEquitySource.ParsePage(new(2026,10,31),1,System.Text.Encoding.UTF8.GetBytes(bad.ToJsonString()),known),"披露主体不符拒绝");
        Reject(()=>AutomaticEquitySource.ValidatePost(new("https://www.cninfo.com.cn/new/other"),AutomaticEquitySource.Request(new(2026,10,31),1)),"只开放固定目录查询合同");
        var temp=Path.Combine(Path.GetTempPath(),"CRS.AutomaticEquity",Guid.NewGuid().ToString("N"));var data=Path.Combine(temp,"data");var raw=new RawEvidenceStore(Path.Combine(temp,"raw"));
        using(var repo=TestMarketData.Open(data,raw))
        {
            repo.SaveAutomaticEquitySecurities(original);repo.BeginAutomaticEquityWindow(new(2026,10,31));
            var fixture=JsonSerializer.SerializeToUtf8Bytes(new{totalAnnouncement=1,hasMore=false,announcements=new[]{new{secCode=doc.Ticker,orgId=doc.OrgId,announcementId=doc.Id,announcementTitle=doc.Title,announcementTime=CRS.Timing.BeijingClock.At(doc.Published,TimeOnly.MinValue).ToUnixTimeMilliseconds(),adjunctUrl=new Uri(doc.Url).AbsolutePath.TrimStart('/'),adjunctType="PDF",adjunctSize=209}}});
            repo.SaveAutomaticEquityPage(fixture);Check(repo.AutomaticEquityStatus().Pending==1,"目录只入队，不冒充已核持仓");
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString()))
            {
                db.Open();using var q=db.CreateCommand();q.CommandText="DELETE FROM equity_watch WHERE ticker='688012'; CREATE TRIGGER fail_auto BEFORE INSERT ON equity_account_identity BEGIN SELECT RAISE(ABORT,'fixture'); END";q.ExecuteNonQuery();
                try{repo.ImportAutomaticEquityDocument(doc.Id,pdf);throw new Exception("事务故障未发生");}catch(SqliteException){}
                Check(repo.EquityDisclosures(new("SSE","688012")).Count==0&&repo.AutomaticEquityStatus().Pending==1&&!repo.EquityWatchlist().Any(x=>x.Ticker=="688012"),"审核写入失败回滚持仓、自动新股票及队列终态");
                q.CommandText="DROP TRIGGER fail_auto";q.ExecuteNonQuery();
            }
            Check(repo.ImportAutomaticEquityDocument(doc.Id,pdf)=="Verified"&&repo.EquityWatchlist().Any(x=>x.Ticker=="688012"),"原队列完成后自动新增持仓股票，无人工确认");
            Check(repo.ImportAutomaticEquityDocument(doc.Id,pdf)=="Verified"&&repo.EquityDisclosures(new("SSE","688012")).Count==1,"重放不重复金融事实");
            var row=repo.EquityHoldings("688012",null,null,false).Single();
            Check(row.Shares==28314016&&row.Nature=="Unknown"&&row.Delta is null&&row.EvidenceLevel==EvidenceLevel.DisclosedHolding,"自动入库仍保持未知用途与无期间交易差额");
        }
        using var reopened=TestMarketData.Open(data,raw);
        Check(reopened.AutomaticEquityStatus().Verified==1&&reopened.AutomaticEquityStatus().Pending==0&&reopened.EquityHoldings("688012",null,null,false).Count==1,"重启保留检索水位、自动股票及核验事实");
    }
}
