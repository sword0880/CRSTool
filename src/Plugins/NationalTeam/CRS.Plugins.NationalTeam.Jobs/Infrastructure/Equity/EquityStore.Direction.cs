// 直接持股方向的复核存储，绑定已接受披露、分类版本、页码及报告日价格；同一命令不得换参数或覆盖旧确认。
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EquityStore
{
    private void InitializeDirectionSchema()
    {
        var version=Scalar("SELECT version FROM feature_schema_migration WHERE feature='equity-direction'");
        var exists=Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='equity_direction_review'"))==1;
        if(version is not null&&(Convert.ToInt32(version)!=1||!exists)||version is null&&exists)
            throw new InvalidDataException("持仓方向核验库版本或迁移记录不完整，拒绝自动重建。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS equity_direction_review(id TEXT PRIMARY KEY,disclosure_id TEXT NOT NULL REFERENCES equity_disclosure(id),request_hash TEXT NOT NULL,payload TEXT NOT NULL,document_hash TEXT NOT NULL,reviewed_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_equity_direction_disclosure ON equity_direction_review(disclosure_id);
            INSERT OR IGNORE INTO feature_schema_migration VALUES('equity-direction',1,$now);
            """,("$now",Now));transaction.Commit();
    }
    private static string DirectionBinding(EquityDirectionReview review)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(review)));
    private static void ValidateDirectionReview(EquityDirectionReview review,EquityDisclosure disclosure,string originalHash)
    {
        static bool Text(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value==value.Trim()&&!value.Any(char.IsControl);
        if(!review.OriginalConfirmed||disclosure.State!="Accepted"||disclosure.ReportAsOf is null||disclosure.Published is null
            ||!string.Equals(review.DocumentHash,originalHash,StringComparison.OrdinalIgnoreCase)||originalHash.Length!=64||!originalHash.All(Uri.IsHexDigit)
            ||!Text(review.Scheme,100)||!Text(review.SchemeVersion,100)||!Text(review.SectorCode,100)||!Text(review.SectorName,100)
            ||review.Style is not null&&!Text(review.Style,100)||!Text(review.Page,100)||!Text(review.Reviewer,100)||!Text(review.Reason,500)
            ||review.ClosingPriceCny is not null&&(review.ClosingPriceCny is <=0||review.PriceDate!=disclosure.ReportAsOf||!Text(review.PricePage,100))
            ||review.ClosingPriceCny is null&&(review.PriceDate is not null||review.PricePage is not null))
            throw new InvalidDataException("方向核验须绑定已核披露原件、分类标准版本及页码；价格只能登记同原文明确披露的报告截止日A股未复权人民币收盘价。");
    }
    private string DirectionDocumentHash(string disclosureId)=>Scalar("SELECT f.sha256 FROM equity_disclosure d JOIN source_fetch f ON f.fetch_id=d.document_fetch_id WHERE d.id=$id",("$id",disclosureId)) as string
        ??throw new InvalidDataException("披露尚未登记核验原件。");
    // 方向审核绑定原请求指纹与披露原件，分类和价格条件全部满足后才记录，重放不能换参数。
    public void ReviewEquityDirection(EquityDirectionReview review,Guid commandId)
    {
        if(commandId==Guid.Empty)throw new InvalidDataException("方向核验须有稳定请求身份。");
        var binding=DirectionBinding(review);var priorBinding=Scalar("SELECT request_hash FROM equity_direction_review WHERE id=$id",("$id",commandId.ToString("N"))) as string;
        if(priorBinding is not null){if(priorBinding!=binding)throw new InvalidDataException("同一方向核验请求不能改变参数。");return;}
        if(Committed(commandId))throw new InvalidDataException("请求已用于其他业务。");
        ValidateEvidence();beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        var key=ReadDirectionKey(review.DisclosureId);var disclosure=ReadEquityDisclosures(key).Single(x=>x.Id==review.DisclosureId);
        var original=DirectionDocumentHash(review.DisclosureId);ValidateDirectionReview(review,disclosure,original);
        if(!raw.Verify(original))throw new InvalidDataException("方向核验原件已损坏。");
        var prior=Scalar("SELECT id FROM equity_direction_review WHERE disclosure_id=$id ORDER BY rowid DESC LIMIT 1",("$id",review.DisclosureId)) as string;
        if(prior!=review.PreviousReviewId)throw new InvalidDataException("方向核验基线已变化，请重新读取后确认更正。");
        Execute("INSERT INTO equity_direction_review VALUES($id,$d,$h,$payload,$original,$now)",("$id",commandId.ToString("N")),("$d",review.DisclosureId),("$h",binding),("$payload",JsonSerializer.Serialize(review)),("$original",original),("$now",Now));
        RecordCommand(commandId,"equity-direction-review");operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    private EtfKey ReadDirectionKey(string id)
    {
        using var command=Command("SELECT market,ticker FROM equity_disclosure WHERE id=$id",("$id",id));using var reader=command.ExecuteReader();
        if(!reader.Read())throw new InvalidDataException("披露不存在。");return new(reader.GetString(0),reader.GetString(1));
    }
    private EquityDirectionFact? ReadDirectionFact(EquityDisclosure disclosure,string original)
    {
        using var command=Command("SELECT id,request_hash,payload,document_hash,reviewed_at FROM equity_direction_review WHERE disclosure_id=$id ORDER BY rowid DESC LIMIT 1",("$id",disclosure.Id));using var reader=command.ExecuteReader();
        if(!reader.Read())return null;
        var review=JsonSerializer.Deserialize<EquityDirectionReview>(reader.GetString(2))??throw new InvalidDataException("方向核验记录无效。");
        if(review.DisclosureId!=disclosure.Id||DirectionBinding(review)!=reader.GetString(1)||!reader.GetString(3).Equals(original,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("方向核验记录与披露原件不一致。");
        ValidateDirectionReview(review,disclosure,original);
        return new(reader.GetString(0),disclosure.Id,original,disclosure.ReportAsOf!.Value,review.Scheme,review.SchemeVersion,review.SectorCode,review.SectorName,review.Style,review.Page,
            review.ClosingPriceCny,review.PriceDate,review.PricePage,DateTimeOffset.Parse(reader.GetString(4),CultureInfo.InvariantCulture));
    }
    public EquityDirectionReport EquityDirection(DateOnly? reportAsOf)
    {
        var observed=CRS.Timing.BeijingClock.Normalize(session.ReadSnapshotCutoff??clock.GetUtcNow());var today=DateOnly.FromDateTime(observed.DateTime);
        if(reportAsOf>today)throw new InvalidDataException("报告期不能在未来。");
        if(Convert.ToInt32(Scalar("SELECT version FROM feature_schema_migration WHERE feature='equity-direction'"))!=1)throw new InvalidDataException("方向报告库版本缺失或不兼容。");
        ValidateEvidence();var watches=EquityWatchlist();session.CheckReadLimit(watches.Count);var disclosures=new Dictionary<EtfKey,List<EquityDisclosure>>();
        foreach(var watch in watches.Where(x=>x.Active))
        {
            operationCancellation.ThrowIfCancellationRequested();disclosures.Add(new(watch.Market,watch.Ticker),ReadEquityDisclosures(new(watch.Market,watch.Ticker)));
            session.CheckReadLimit(disclosures.Sum(x=>x.Value.Count));
        }
        reportAsOf??=disclosures.Values.SelectMany(x=>x).Where(x=>x.State is "Accepted" or "Critical"&&x.ReportAsOf<=today&&x.Published<=today)
            .Select(x=>x.ReportAsOf).Max();
        var rows=new List<EquityHoldingRow>();var facts=new List<EquityDirectionFact>();var gaps=new List<EquityDirectionGap>();var verified=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        foreach(var watch in watches.Where(x=>x.Active))
        {
            operationCancellation.ThrowIfCancellationRequested();var periods=disclosures[new(watch.Market,watch.Ticker)].Where(x=>x.ReportAsOf==reportAsOf&&reportAsOf is not null&&x.State is "Accepted" or "Critical").ToArray();
            if(periods.Length>1){gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"ConflictingDisclosures","同报告期存在多份有效披露，整只证券暂不汇总。"));continue;}
            if(periods.SingleOrDefault() is not {} period)continue;
            if(period.State!="Accepted"||period.Published>today){gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"EvidenceUnavailable","披露证据异常或尚未公开。"));continue;}
            var original=DirectionDocumentHash(period.Id);
            if(!verified.TryGetValue(original,out var valid)){valid=raw.Verify(original);verified.Add(original,valid);}
            if(!valid){gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"EvidenceUnavailable","原件即时校验失败，不沿用巡检前的旧结论。"));continue;}
            var periodRows=EquityPeriodRows(watch,period,null).ToArray();
            rows.AddRange(periodRows.Where(x=>x.EvidenceHash?.Equals(original,StringComparison.OrdinalIgnoreCase)==true));
            if(periodRows.Any(x=>x.EvidenceLevel!=EvidenceLevel.DisclosedHolding))gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"UnreviewedAccounts","该期部分具名账户尚未核验，不能扩大为完整机构组合。"));
            try{if(ReadDirectionFact(period,original) is {} fact)facts.Add(fact);}
            catch(Exception error) when(error is InvalidDataException or JsonException or FormatException){gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"ClassificationEvidenceUnavailable","方向核验记录不完整或与当前原件不符。"));}
            session.CheckReadLimit(rows.Count);
        }
        return EquityDirectionProjection.Build(reportAsOf,observed,watches,rows,facts,gaps);
    }
}
