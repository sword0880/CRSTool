using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EquityStore
{
    public string ImportAutomaticEquityDocument(string id,byte[] original)
    {
        using var command=Command("SELECT a.metadata,a.state,f.sha256 FROM equity_auto_document a JOIN source_fetch f ON f.fetch_id=a.fetch_id WHERE a.id=$id",("$id",id));
        AutomaticEquityDocument document;string state,indexHash;
        using(var reader=command.ExecuteReader())
        {
            if(!reader.Read())throw new InvalidDataException("原文不在已核官方目录，不能自动登记。");
            document=JsonSerializer.Deserialize<AutomaticEquityDocument>(reader.GetString(0))??throw new InvalidDataException("自动目录身份损坏。");
            state=reader.GetString(1);indexHash=reader.GetString(2);
        }
        if(state!="Queued")return state;
        if(!raw.Verify(indexHash)||document.Id!=id||document.Ticker=="603195"||original.Length is <5 or >8*1024*1024||!original.AsSpan(0,5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("官方目录证据或PDF字节身份无效。");
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime);
        if(document.Published>today||document.ReportAsOf>document.Published)throw new InvalidDataException("报告日期在未来。");
        var watch=Scalar("SELECT active FROM equity_watch WHERE market=$m AND ticker=$t",("$m",document.Market),("$t",document.Ticker));
        if(watch is not null&&Convert.ToInt32(watch)==0){SetAutomaticTerminal(id,"Inactive",null,null,null,"尊重已停用观察配置");return "Inactive";}
        var hash=raw.Save(original);
        using(var existing=Command("""
            SELECT d.id,f.sha256,p.sha256,p.source_url FROM equity_disclosure d
            JOIN source_fetch f ON f.fetch_id=d.fetch_id LEFT JOIN source_fetch p ON p.fetch_id=d.document_fetch_id
            WHERE d.market=$m AND d.ticker=$t AND d.report_date=$d AND d.state='Accepted'
            """,("$m",document.Market),("$t",document.Ticker),("$d",document.ReportAsOf.ToString("yyyy-MM-dd"))))
        using(var reader=existing.ExecuteReader())
        {
            if(reader.Read())
            {
                var disclosure=reader.GetString(0);
                var same=!reader.IsDBNull(2)&&reader.GetString(2)==hash&&reader.GetString(3)==document.Url&&raw.Verify(reader.GetString(1));
                reader.Close();
                SetAutomaticTerminal(id,same?"AlreadyVerified":"NeedsReview",hash,null,disclosure,same?null:"同报告期原文与既有已核证据不同，保留既有快照并转异常");
                return same?"AlreadyVerified":"NeedsReview";
            }
        }
        AutomaticEquityProof proof;
        try{proof=AutomaticEquityPdf.Parse(document,original,operationCancellation);}
        catch(InvalidDataException error){RecordAutomaticEquityIssue(id,document.Ticker+" "+error.Message,original);return "NeedsReview";}
        if(proof.Accounts.Count!=proof.Holders.Count||proof.Accounts.Select(x=>x.Account).Distinct().Count()!=proof.Accounts.Count
            ||proof.Holders.Any(x=>x.Shares<=0||x.Shares!=decimal.Truncate(x.Shares)||x.Percent is not null))
            throw new InvalidDataException("自动核验子集身份、股数或比例口径无效。");
        return CommitAutomaticEquity(document,indexHash,hash,proof,watch is not null);
    }
    private void SetAutomaticTerminal(string id,string state,string? hash,string? extract,string? disclosure,string? reason)
    {
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("UPDATE equity_auto_document SET state=$s,original_hash=$h,extract_hash=$e,disclosure_id=$d,issue=$i,updated=$n WHERE id=$id AND state='Queued'",
            ("$id",id),("$s",state),("$h",hash),("$e",extract),("$d",disclosure),("$i",reason),("$n",Now));
        Execute("UPDATE equity_auto_state SET updated=$n,last_issue=$i WHERE id=1",("$n",Now),("$i",reason));transaction.Commit();
    }
    private string CommitAutomaticEquity(AutomaticEquityDocument document,string indexHash,string original,AutomaticEquityProof proof,bool watched)
    {
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM equity_disclosure WHERE market=$m AND ticker=$t AND report_date=$d AND state='Accepted'",
            ("$m",document.Market),("$t",document.Ticker),("$d",document.ReportAsOf.ToString("yyyy-MM-dd"))))>0)
            throw new InvalidDataException("同报告期核验基线已变化，保留原队列重新核查。");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Parser=AutomaticEquityPdf.Version,DirectoryHash=indexHash,Document=document,OriginalHash=original,Proof=proof,Scope="AutomaticReviewedAShareSubset",Nature="Unknown",Comparable=false});
        var normalized=raw.Save(bytes);var originalFetch=RecordFetch(original,document.Url,original,"OfficialCninfoEquityPdf",AutomaticEquityPdf.Version);
        var extractFetch=RecordFetch(normalized,document.Url,original,"OfficialCninfoEquityPdf",AutomaticEquityPdf.Version+"-extract");
        string? disclosure=null;var actor="自动原文交叉核验/"+AutomaticEquityPdf.Version;
        if(proof.Accounts.Count>0||watched)
        {
            if(proof.Accounts.Count>0)Execute("INSERT OR IGNORE INTO equity_watch VALUES($m,$t,$n,1,0)",("$m",document.Market),("$t",document.Ticker),("$n",document.Name));
            disclosure=Guid.NewGuid().ToString("N");var now=Now;
            Execute("""
                INSERT INTO equity_disclosure(id,market,ticker,source_date,fetch_id,document_fetch_id,report_date,published,state,comparable,nature,share_class,basis,page,reviewer,verified_at)
                VALUES($id,$m,$t,$sd,$f,$pf,$d,$p,'Accepted',0,'Unknown','A','TotalShares',$page,$who,$now)
                """,("$id",disclosure),("$m",document.Market),("$t",document.Ticker),("$sd",DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime).ToString("yyyy-MM-dd")),
                ("$f",extractFetch),("$pf",originalFetch),("$d",document.ReportAsOf.ToString("yyyy-MM-dd")),("$p",document.Published.ToString("yyyy-MM-dd")),("$page",proof.Pages),("$who",actor),("$now",now));
            foreach(var holder in proof.Holders)Execute("INSERT INTO equity_holder VALUES($id,$a,$s,NULL)",("$id",disclosure),("$a",holder.Account),("$s",Decimal(holder.Shares)));
            var baseline=Scalar("SELECT id FROM equity_disclosure WHERE market=$m AND ticker=$t AND report_date<$d AND state='Accepted' ORDER BY report_date DESC LIMIT 1",("$m",document.Market),("$t",document.Ticker),("$d",document.ReportAsOf.ToString("yyyy-MM-dd"))) as string;
            foreach(var account in proof.Accounts)
            {
                operationCancellation.ThrowIfCancellationRequested();var audit=Guid.NewGuid().ToString("N");
                Execute("INSERT INTO equity_account_review VALUES($id,$a,'Unknown')",("$id",disclosure),("$a",account.Account));
                Execute("INSERT INTO equity_account_identity VALUES($id,$a,$g,$who)",("$id",disclosure),("$a",account.Account),("$g",account.Institution),("$who",actor));
                Execute("INSERT INTO equity_account_comparability VALUES($id,$a,0)",("$id",disclosure),("$a",account.Account));
                Execute("INSERT INTO equity_account_review_audit VALUES($audit,$id,$a,$g,'Unknown',0,$who,$now,$h,$p)",("$audit",audit),("$id",disclosure),("$a",account.Account),("$g",account.Institution),("$who",actor),("$now",now),("$h",original),("$p",account.Page));
                Execute("INSERT INTO equity_account_comparison_baseline VALUES($audit,$prior)",("$audit",audit),("$prior",baseline));
            }
            Execute("INSERT INTO equity_review_audit VALUES($a,$id,$who,$now,$f,$d,$p,$page)",("$a",Guid.NewGuid().ToString("N")),("$id",disclosure),("$who",actor),("$now",now),("$f",originalFetch),("$d",document.ReportAsOf.ToString("yyyy-MM-dd")),("$p",document.Published.ToString("yyyy-MM-dd")),("$page",proof.Pages));
        }
        var issue=proof.Issues is {Count:>0}?document.Ticker+" "+string.Join("；",proof.Issues):null;
        if(issue?.Length>500)issue=issue[..500];
        var outcome=proof.Accounts.Count>0?issue is null?"Verified":"VerifiedWithExceptions":"NoMatch";
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id IN ($a,$b)",("$a",originalFetch),("$b",extractFetch));
        Execute("UPDATE equity_auto_document SET state=$s,original_hash=$h,extract_hash=$e,disclosure_id=$d,issue=$i,updated=$n WHERE id=$id AND state='Queued'",
            ("$id",document.Id),("$s",outcome),("$h",original),("$e",normalized),("$d",disclosure),("$i",issue),("$n",Now));
        Execute("UPDATE equity_auto_state SET updated=$n,last_issue=$i WHERE id=1",("$n",Now),("$i",issue));operationCancellation.ThrowIfCancellationRequested();transaction.Commit();return outcome;
    }
}
