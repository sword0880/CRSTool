using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>公开年报原件的隔离登记及故障反例；合成当前/前期仅验证基线规则，不替代真实可比性审核。</summary>
internal static class HistoricalEquityChecks
{
    private const string Account="基本养老保险基金一零零三组合";
    private static readonly EtfKey Key=new("SSE","600153");
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS 历史PDF："+label);}
    private static void Reject(Action action,string label)
    {try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception("未拒绝："+label);}
    private static SqliteConnection Sql(string data)
    {var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Password=Convert.ToHexString(TestMarketData.Key),Pooling=false}.ToString());db.Open();return db;}
    private static long Count(string data,string table)
    {using var db=Sql(data);using var q=db.CreateCommand();q.CommandText="SELECT count(*) FROM "+table;return Convert.ToInt64(q.ExecuteScalar());}
    public static void CatalogContracts(string root)
    {
        var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,new RawEvidenceStore(Path.Combine(root,"raw")));
        var definition=repo.HistoricalEquityDocuments().Single();
        Check(definition.Ticker=="600153"&&definition.ReportAsOf==new DateOnly(2025,12,31)&&definition.HolderCount==10&&definition.ReviewedAccounts==4,"完整前十表和四个具名账户分开，目录不是已登记持仓");
        Check(definition.Published==new DateOnly(2026,4,29)&&definition.PublicationBasis.Contains("不代表最早")&&definition.OriginalHash=="470448A5B8073540B0A6A722E1DDED1558AB9DC06FB44347D0E91D2948B72DF2","原文摘要、官网列示日及非最早时刻边界明确");
        Reject(()=>repo.ImportHistoricalEquityPdf("unregistered",new byte[5],"test","test",Guid.NewGuid()),"未知目录不能导入任意PDF");
        Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,"%PDF-synthetic"u8.ToArray(),"test","test",Guid.NewGuid()),"默认正式入口拒绝伪PDF及未匹配固定字节的摘录");
        Check(repo.HistoricalEquityReceipt(Guid.NewGuid()) is null&&!repo.HistoricalEquityRequestMatches(Guid.NewGuid(),definition.Id,"test","test"),"缺失业务回执保持未知，不伪造成功");
        Check(Count(data,"equity_historical_document")==0&&Count(data,"worker_business_command")==0,"目录查询和拒绝请求不生成持仓或命令成功记录");
    }
    public static void Run(string originalPdf)
    {
        var bytes=File.ReadAllBytes(originalPdf);var root=Path.Combine(Path.GetTempPath(),"CRS.HistoricalPdf",Guid.NewGuid().ToString("N"));
        CatalogContracts(Path.Combine(root,"contracts"));var data=Path.Combine(root,"positive/data");var rawPath=Path.Combine(root,"positive/raw");
        using(var repo=TestMarketData.Open(data,new RawEvidenceStore(rawPath)))
        {
            var definition=repo.HistoricalEquityDocuments().Single();var id=Guid.NewGuid();
            Check(Convert.ToHexString(SHA256.HashData(bytes))==definition.OriginalHash,"真实518页年报字节匹配固定来源，不模拟SSE历史响应");
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"","test",id),"审核人缺失拒绝");
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","",id),"理由缺失拒绝");
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",Guid.Empty),"缺稳定请求ID拒绝");
            var altered=bytes.ToArray();altered[^1]^=1;
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,altered,"test","test",id),"原件任意字节改变不继承核验结论");
            var before=CRS.Timing.BeijingClock.Now;var receipt=repo.ImportHistoricalEquityPdf(definition.Id,bytes,"isolated-reviewer","isolated original review",id);
            var disclosure=repo.EquityDisclosures(Key).Single();var events=repo.EquityEvents(Key);
            Check(receipt is{Outcome:"Imported",ReviewedAccounts:4}&&repo.HistoricalEquityReceipt(id)==receipt,"金融事务与独立业务回执同次提交");
            Check(disclosure.ReportAsOf==new DateOnly(2025,12,31)&&disclosure.SourceDate==DateOnly.FromDateTime(before.DateTime)&&receipt.RecordedAt>=before,"报告期不冒充本次取得/审核时点");
            Check(disclosure.Holders.Count==10&&disclosure.Holders.All(x=>x.Account!="全国社保基金六零四组合")&&events.Count==4,"完整十户原表保留，未列示六零四不补零，名义持有人不升级机构归属");
            Check(events.All(x=>x.Nature=="Unknown"&&x.Delta is null&&x.EvidenceLevel==EvidenceLevel.DisclosedHolding&&x.EvidenceHash==definition.OriginalHash&&x.EvidencePage?.Contains("P15")==true),"四账户逐项页码和原件证据，不默认证券投资或批准差额");
            var audits=Count(data,"equity_account_review_audit");
            Check(repo.ImportHistoricalEquityPdf(definition.Id,bytes,"isolated-reviewer","isolated original review",id)==receipt&&Count(data,"equity_account_review_audit")==audits,"原ID重放不重复审核和金融事实");
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"changed","isolated original review",id),"原ID不能更改审核人或参数");
            Check(repo.HistoricalEquityRequestMatches(id,definition.Id,"isolated-reviewer","isolated original review")&&!repo.HistoricalEquityRequestMatches(id,definition.Id,"changed","isolated original review"),"业务重放参数身份独立核对");
            var reused=repo.ImportHistoricalEquityPdf(definition.Id,bytes,"other-reviewer","same immutable document",Guid.NewGuid());
            Check(reused.Outcome=="AlreadyRegistered"&&reused.DisclosureId==receipt.DisclosureId&&Count(data,"source_fetch")==2&&Count(data,"equity_account_review_audit")==audits,"新请求复用既有历史原文，不重建事实或伪造首见时间");
            var collision=Guid.NewGuid();var config=repo.Configuration().Single(x=>x.Ticker=="510300");
            repo.SaveConfiguration(new("SSE","510300"),config.Name,config.IndexGroup,config.Active,"isolated",config.Revision,collision);
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",collision),"其他业务请求ID不能转作历史登记");
            File.WriteAllText(Path.Combine(rawPath,definition.OriginalHash+".raw"),"damaged");
            Reject(()=>repo.EquityEvents(Key),"新原件损坏时金融读取失败关闭");
            Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"new-reviewer","do not repair silently",Guid.NewGuid()),"不把损坏既有原件的复用写成新成功回执");
            repo.ReconcileEvidenceIntegrity();Check(repo.EquityEvents(Key).All(x=>x.State=="Critical"&&x.Shares is null),"后台确认损坏后只展示异常，不展示零持股");
        }
        Rollback(Path.Combine(root,"rollback"),bytes);FileFailure(Path.Combine(root,"file-failure"),bytes);Baseline(Path.Combine(root,"baseline"),bytes);ExistingPeriod(Path.Combine(root,"existing"),bytes);
        Console.WriteLine("历史PDF隔离与真实原件检查结束；不代表正式Worker已安装或事实已登记。现场："+root);
    }
    private static void Rollback(string root,byte[] bytes)
    {
        var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,new RawEvidenceStore(Path.Combine(root,"raw")));var definition=repo.HistoricalEquityDocuments().Single();var id=Guid.NewGuid();
        using(var db=Sql(data)){using var q=db.CreateCommand();q.CommandText="CREATE TRIGGER fail_history_receipt BEFORE INSERT ON equity_historical_receipt BEGIN SELECT RAISE(ABORT,'synthetic receipt failure');END";q.ExecuteNonQuery();}
        try{repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",id);throw new Exception("预期故障未发生。");}catch(SqliteException){}
        Check(Count(data,"equity_disclosure")==0&&Count(data,"source_fetch")==0&&Count(data,"equity_account_review_audit")==0&&Count(data,"worker_business_command")==0&&repo.HistoricalEquityReceipt(id) is null,"业务回执落库失败回滚所有金融、来源、审核和命令记录");
        using(var db=Sql(data)){using var q=db.CreateCommand();q.CommandText="DROP TRIGGER fail_history_receipt";q.ExecuteNonQuery();}
        Check(repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",id).Outcome=="Imported","故障解除后沿原ID完成一次事务");
    }
    private static void FileFailure(string root,byte[] bytes)
    {
        var writes=0;var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"),()=>{if(++writes==2)throw new IOException("synthetic disk failure");});
        using var repo=TestMarketData.Open(data,raw);var definition=repo.HistoricalEquityDocuments().Single();var id=Guid.NewGuid();
        try{repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",id);throw new Exception("预期磁盘故障未发生。");}catch(IOException){}
        Check(Count(data,"equity_disclosure")==0&&Count(data,"source_fetch")==0&&repo.HistoricalEquityReceipt(id) is null,"存原件后摘录失败只可留未引用文件，不能留部分持仓或成功回执");
        using var stop=new CancellationTokenSource();stop.Cancel();try{repo.ExecuteOperation(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","test",id),stop.Token);throw new Exception("取消未生效。");}catch(OperationCanceledException){}
        Check(Count(data,"worker_business_command")==0,"取消不制造命令已提交");
    }
    private static void Baseline(string root,byte[] bytes)
    {
        using var repo=TestMarketData.Open(Path.Combine(root,"data"),new RawEvidenceStore(Path.Combine(root,"raw")));var definition=repo.HistoricalEquityDocuments().Single();
        string Add(DateOnly report,DateOnly published,decimal shares,string tag,string? baseline)
        {
            // 两份合成SSE当前响应只作独立旧基线夹具；历史年报仍走真实固定PDF入口。
            var source=JsonSerializer.SerializeToUtf8Bytes(new{sqlId="COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",actionErrors=Array.Empty<string>(),pageHelp=new{total=1},result=new[]{new{REPORT_DATE=published.ToString("yyyy-MM-dd"),STOCK_ID="600153",NAME=Account,NUMBER_END=shares,RATIO=1m}}});
            repo.SaveEquityDisclosure(Key.Market,Key.Ticker,OfficialEquitySource.Request(Key.Market,Key.Ticker),source);var row=repo.EquityDisclosures(Key).Single(x=>x.State=="NeedsReview");
            repo.ReviewEquity(new(row.Id,report,published,"https://www.sse.com.cn/fixture-"+tag+".pdf","P1 synthetic","fixture",true,"Portfolio",Account:Account,OriginalConfirmed:true){ComparisonDisclosureId=baseline},Encoding.UTF8.GetBytes("%PDF-"+tag));return row.Id;
        }
        var old=Add(new(2024,12,31),new(2025,4,1),1,"prior",null);var current=Add(new(2026,6,30),new(2026,8,29),2,"current",old);
        Check(repo.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account).Delta==1,"合成旧前期可比批准生效，建立失效反例基线");
        var imported=repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","insert missing period",Guid.NewGuid());
        var row=repo.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account);
        Check(row.DisclosureId==current&&row.Shares==2&&row.Delta is null&&row.ComparisonFrom==new DateOnly(2025,12,31),"真实历史补入不改本期股数，使原可比基线失效");
        repo.ReviewEquity(new(imported.DisclosureId,definition.ReportAsOf,definition.Published,definition.DocumentUrl,"P158","fixture",true,"Portfolio",Account:Account,OriginalConfirmed:true){ComparisonDisclosureId=old},bytes);
        Check(repo.EquityHoldings(null,null,null,false).Single(x=>x.Account==Account).Delta is null,"即便前期后来另行批准，本期仍不能继承过期基线");
    }
    private static void ExistingPeriod(string root,byte[] bytes)
    {
        var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,new RawEvidenceStore(Path.Combine(root,"raw")));var definition=repo.HistoricalEquityDocuments().Single();
        var source=JsonSerializer.SerializeToUtf8Bytes(new{sqlId="COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS",actionErrors=Array.Empty<string>(),pageHelp=new{total=1},result=new[]{new{REPORT_DATE="2026-04-29",STOCK_ID="600153",NAME=Account,NUMBER_END=67250642m,RATIO=2.32m}}});
        repo.SaveEquityDisclosure(Key.Market,Key.Ticker,OfficialEquitySource.Request(Key.Market,Key.Ticker),source);var existing=repo.EquityDisclosures(Key).Single();
        repo.ReviewEquity(new(existing.Id,definition.ReportAsOf,definition.Published,definition.DocumentUrl,"P158","fixture",false,"Unknown",Account:Account,OriginalConfirmed:true),bytes);
        var id=Guid.NewGuid();Reject(()=>repo.ImportHistoricalEquityPdf(definition.Id,bytes,"test","do not overwrite existing",id),"其他入口已有同报告期时拒绝静默覆盖");
        Check(repo.EquityDisclosures(Key).Single().Id==existing.Id&&repo.HistoricalEquityReceipt(id) is null&&Count(data,"equity_historical_document")==0,"拒绝冲突保留原披露和未知回执");
    }
}
