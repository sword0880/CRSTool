using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IHistoricalEquityReadRepository,IHistoricalEquityWriteRepository
{
    private void InitializeHistoricalEquitySchema()
    {
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS equity_historical_document(definition_id TEXT PRIMARY KEY,definition_hash TEXT NOT NULL,disclosure_id TEXT NOT NULL UNIQUE REFERENCES equity_disclosure(id),original_hash TEXT NOT NULL,extract_hash TEXT NOT NULL,registered_at TEXT NOT NULL,publication_basis TEXT NOT NULL,source_scope TEXT NOT NULL,holder_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS equity_historical_receipt(request_id TEXT PRIMARY KEY,request_hash TEXT NOT NULL,definition_id TEXT NOT NULL REFERENCES equity_historical_document(definition_id),disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),outcome TEXT NOT NULL,reviewed_accounts INTEGER NOT NULL,recorded_at TEXT NOT NULL,original_hash TEXT NOT NULL,extract_hash TEXT NOT NULL,reviewer TEXT NOT NULL,reason TEXT NOT NULL);
            """);
        var version=Scalar("SELECT version FROM feature_schema_migration WHERE feature='historical-equity-pdf'");
        if(version is not null&&Convert.ToInt32(version)!=1)throw new InvalidDataException("历史原文库版本不兼容。");
        Execute("INSERT OR IGNORE INTO feature_schema_migration VALUES('historical-equity-pdf',1,$now)",("$now",Now));transaction.Commit();
    }
    public IReadOnlyList<HistoricalEquityPdf> HistoricalEquityDocuments()=>HistoricalEquityCatalog.Documents();
    private static string HistoricalBinding(string definitionId,string reviewer,string reason)
        =>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{definitionId,reviewer=reviewer.Trim(),reason=reason.Trim()})));
    public bool HistoricalEquityRequestMatches(Guid requestId,string definitionId,string reviewer,string reason)
        =>Scalar("SELECT request_hash FROM equity_historical_receipt WHERE request_id=$id",("$id",requestId.ToString("N"))) as string==HistoricalBinding(definitionId,reviewer,reason);
    public HistoricalEquityReceipt? HistoricalEquityReceipt(Guid requestId)
    {
        using var command=Command("SELECT definition_id,disclosure_id,outcome,reviewed_accounts,recorded_at,original_hash,extract_hash FROM equity_historical_receipt WHERE request_id=$id",("$id",requestId.ToString("N")));
        using var reader=command.ExecuteReader();return !reader.Read()?null:new(requestId,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),CRS.Timing.BeijingClock.Parse(reader.GetString(4)),reader.GetString(5),reader.GetString(6));
    }
    public HistoricalEquityReceipt ImportHistoricalEquityPdf(string definitionId,byte[] document,string reviewer,string reason,Guid requestId)
    {
        var (definition,extract,extractHash)=HistoricalEquityCatalog.Get(definitionId);
        reviewer=reviewer.Trim();reason=reason.Trim();operationCancellation.ThrowIfCancellationRequested();
        var documentHash=Convert.ToHexString(SHA256.HashData(document));
        if(requestId==Guid.Empty||reviewer.Length is <1 or >100||reason.Length is <1 or >500
            ||definition.Published>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime)
            ||document.Length<5||!document.AsSpan(0,5).SequenceEqual("%PDF-"u8)||documentHash!=definition.OriginalHash)
            throw new InvalidDataException("原文身份、报告日期、审核人或理由无效；拒绝更改已核摘录的金融值。");
        var requestHash=HistoricalBinding(definitionId,reviewer,reason);
        if(HistoricalEquityReceipt(requestId) is {} priorReceipt)
        {
            if(Scalar("SELECT request_hash FROM equity_historical_receipt WHERE request_id=$id",("$id",requestId.ToString("N"))) as string!=requestHash||priorReceipt.ExtractHash!=extractHash)
                throw new InvalidDataException("同一请求不能更改历史原文定义或审核参数。");
            return priorReceipt;
        }
        if(Committed(requestId))throw new InvalidDataException("请求已用于其他业务，不能复用为历史登记。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var disclosure=Scalar("SELECT disclosure_id FROM equity_historical_document WHERE definition_id=$id",("$id",definitionId)) as string;
        var outcome="AlreadyRegistered";
        if(disclosure is null)
        {
            if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_disclosure WHERE market=$m AND ticker=$t AND report_date=$d AND state='Accepted'",("$m",definition.Market),("$t",definition.Ticker),("$d",definition.ReportAsOf.ToString("yyyy-MM-dd"))))>0)
                throw new InvalidDataException("同报告期已有已核披露，须显式更正审核，不能自动覆盖。");
            if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_watch WHERE market=$m AND ticker=$t",("$m",definition.Market),("$t",definition.Ticker)))!=1)
                throw new InvalidDataException("证券尚未登记在持股观察目录。");
            // 原PDF和规范化摘录分别存证；文件故障可留下未引用原件，但事务失败不会留下部分金融事实或成功回执。
            var original=raw.Save(document);var normalized=raw.Save(extract);operationCancellation.ThrowIfCancellationRequested();
            var originalFetch=RecordFetch(original,definition.DocumentUrl,original,"ReviewedEquityDisclosurePdf","reviewed-historical-pdf-original-v1");
            var extractFetch=RecordFetch(normalized,definition.DocumentUrl,original,"ReviewedEquityDisclosurePdf","reviewed-historical-pdf-extract-v1");
            disclosure=Guid.NewGuid().ToString("N");var now=Now;
            Execute("INSERT INTO equity_disclosure(id,market,ticker,source_date,fetch_id,document_fetch_id,report_date,published,state,comparable,nature,share_class,basis,page,reviewer,verified_at) VALUES($id,$m,$t,$source,$f,$doc,$d,$p,'Accepted',0,'Unknown','A','TotalShares',$page,$who,$now)",
                ("$id",disclosure),("$m",definition.Market),("$t",definition.Ticker),("$source",DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime).ToString("yyyy-MM-dd")),("$f",extractFetch),("$doc",originalFetch),("$d",definition.ReportAsOf.ToString("yyyy-MM-dd")),("$p",definition.Published.ToString("yyyy-MM-dd")),("$page",definition.Pages),("$who",reviewer),("$now",now));
            foreach(var holder in definition.Holders)
            {operationCancellation.ThrowIfCancellationRequested();Execute("INSERT INTO equity_holder VALUES($id,$a,$s,$p)",("$id",disclosure),("$a",holder.Account),("$s",Decimal(holder.Shares)),("$p",holder.Percent is {} percent?Decimal(percent):null));}
            var baseline=Scalar("SELECT id FROM equity_disclosure WHERE market=$m AND ticker=$t AND report_date<$d AND state='Accepted' ORDER BY report_date DESC LIMIT 1",("$m",definition.Market),("$t",definition.Ticker),("$d",definition.ReportAsOf.ToString("yyyy-MM-dd"))) as string;
            foreach(var account in definition.ReviewedAccounts)
            {
                var audit=Guid.NewGuid().ToString("N");
                // 原文只证明具名账户和股数；性质及跨期可比性保持未知，不因历史补入重批本期基线。
                Execute("INSERT INTO equity_account_review VALUES($id,$a,'Unknown')",("$id",disclosure),("$a",account.Account));
                Execute("INSERT INTO equity_account_identity VALUES($id,$a,$g,$who)",("$id",disclosure),("$a",account.Account),("$g",account.Institution),("$who",reviewer));
                Execute("INSERT INTO equity_account_comparability VALUES($id,$a,0)",("$id",disclosure),("$a",account.Account));
                Execute("INSERT INTO equity_account_review_audit VALUES($audit,$id,$a,$g,'Unknown',0,$who,$now,$h,$p)",("$audit",audit),("$id",disclosure),("$a",account.Account),("$g",account.Institution),("$who",reviewer),("$now",now),("$h",original),("$p",account.Page));
                Execute("INSERT INTO equity_account_comparison_baseline VALUES($audit,$prior)",("$audit",audit),("$prior",baseline));
            }
            Execute("INSERT INTO equity_review_audit VALUES($audit,$id,$who,$now,$f,$d,$p,$page)",("$audit",Guid.NewGuid().ToString("N")),("$id",disclosure),("$who",reviewer),("$now",now),("$f",originalFetch),("$d",definition.ReportAsOf.ToString("yyyy-MM-dd")),("$p",definition.Published.ToString("yyyy-MM-dd")),("$page",definition.Pages));
            Execute("INSERT INTO equity_historical_document VALUES($id,$dh,$d,$oh,$eh,$now,$basis,$scope,$count)",("$id",definitionId),("$dh",extractHash),("$d",disclosure),("$oh",original),("$eh",normalized),("$now",now),("$basis",definition.PublicationBasis),("$scope",definition.Scope),("$count",definition.Holders.Length));
            Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id IN ($one,$two)",("$one",originalFetch),("$two",extractFetch));outcome="Imported";
        }
        else
        {
            if(Scalar("SELECT definition_hash FROM equity_historical_document WHERE definition_id=$id",("$id",definitionId)) as string!=extractHash)
                throw new InvalidDataException("定义身份已改变，不能以同一目录项替换历史事实。");
            var existing=ReadEquityDisclosures(new(definition.Market,definition.Ticker)).Single(x=>x.Id==disclosure);
            if(existing.State!="Accepted"||!raw.Verify(existing.Hash)||!raw.Verify(documentHash))
                throw new InvalidDataException("既有历史披露已被替代或证据异常，不自动恢复旧版或补写成功回执。");
        }
        var recorded=Now;
        Execute("INSERT INTO equity_historical_receipt VALUES($r,$rh,$def,$d,$o,$count,$now,$oh,$eh,$who,$reason)",("$r",requestId.ToString("N")),("$rh",requestHash),("$def",definitionId),("$d",disclosure),("$o",outcome),("$count",definition.ReviewedAccounts.Length),("$now",recorded),("$oh",documentHash),("$eh",extractHash),("$who",reviewer),("$reason",reason));
        RecordCommand(requestId,"equity-history-import");operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
        return new(requestId,definitionId,disclosure,outcome,definition.ReviewedAccounts.Length,CRS.Timing.BeijingClock.Parse(recorded),documentHash,extractHash);
    }
}
