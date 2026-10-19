using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>登记独立官方ETF原件，首次取得时间由后台固定；登记本身不升级具名持仓或可比性。</summary>
    public void RegisterAlertDocument(AlertDocumentRequest request,byte[] document,Guid? commandId=null)
    {
        if(Committed(commandId))return;
        if(!Uri.TryCreate(request.Url,UriKind.Absolute,out var uri)||!OfficialEquityDocuments.IsAllowed(uri)||OfficialEquityDocuments.IsReviewedIssuer(uri))throw new InvalidDataException("ETF证据只接受交易所官方PDF。");
        if(document.Length<5||document.Length>8*1024*1024||!document.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new InvalidDataException("ETF证据不是有效PDF原件。");
        if(!Configuration().Any(x=>x.Market==request.Market&&x.Ticker==request.Ticker))throw new InvalidDataException("ETF未登记。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var hash=raw.Save(document);var id=ExitAlertEvaluator.Hash(request.Market+"|"+request.Ticker+"|"+uri.AbsoluteUri+"|"+hash);
        var fetch=RecordFetch(hash,uri.AbsoluteUri,hash,"ReviewedEtfAlertPdf","human-etf-proof-v1");
        Execute("INSERT OR IGNORE INTO nt_alert_document VALUES($id,$m,$t,$u,$h,$at,$f)",("$id",id),("$m",request.Market),("$t",request.Ticker),("$u",uri.AbsoluteUri),("$h",hash),("$at",Now),("$f",fetch));
        RecordCommand(commandId,"alert-document");transaction.Commit();
    }
    public List<AlertDocument> AlertDocuments(EtfKey key)
    {
        using var command=Command("SELECT id,market,ticker,url,hash,first_seen FROM nt_alert_document WHERE market=$m AND ticker=$t ORDER BY first_seen DESC LIMIT 1000",("$m",key.Market),("$t",key.Ticker));
        using var reader=command.ExecuteReader();var rows=new List<AlertDocument>();while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5)));return rows;
    }
    private AlertDocument RequireAlertDocument(string id)
    {
        using var command=Command("SELECT id,market,ticker,url,hash,first_seen FROM nt_alert_document WHERE id=$id",("$id",id));using var reader=command.ExecuteReader();
        if(!reader.Read())throw new InvalidDataException("先登记并核对官方原件。");
        var row=new AlertDocument(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5));
        if(!raw.Verify(row.Hash))throw new InvalidDataException("原件完整性异常，不能继承旧确认。");return row;
    }
    public void ReviewAlertMetadata(AlertMetadataReview review,Guid? commandId=null)
    {
        if(Committed(commandId))return;var document=RequireAlertDocument(review.DocumentId);
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"China Standard Time").DateTime);
        if(!review.OriginalConfirmed||review.ComparableFrom>review.ComparableThrough||review.ComparableThrough>today
            ||!Identifier(review.FundId)||!Identifier(review.ManagerId)||!Identifier(review.IndexId)||!ReviewerName(review.Reviewer))throw new InvalidDataException("基金标识、管理人、审核期间或原件确认不足。");
        var row=new AlertFundMetadata(document.Market,document.Ticker,review.FundId,review.ManagerId,review.IndexId,review.ComparableFrom,review.ComparableThrough,document.Url,document.Hash,review.Reviewer,true);
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("INSERT INTO nt_alert_metadata VALUES($m,$t,$p) ON CONFLICT(market,ticker) DO UPDATE SET payload=excluded.payload",("$m",row.Market),("$t",row.Ticker),("$p",JsonSerializer.Serialize(row)));
        MarkAlertPending();RecordCommand(commandId,"alert-metadata");transaction.Commit();
    }
    public List<AlertFundMetadata> AlertMetadata()
    {
        using var command=Command("SELECT payload FROM nt_alert_metadata");
        return ReadJson<AlertFundMetadata>(command).Select(x=>raw.Verify(x.EvidenceHash)?x:x with{OriginalConfirmed=false}).ToList();
    }
    public void ReviewNamedEtfHolding(NamedEtfHoldingReview review,Guid? commandId=null)
    {
        if(Committed(commandId))return;var document=RequireAlertDocument(review.DocumentId);
        if(!Identifier(review.LegalEntityId)||review.LegalName.Length is <1 or >150||review.Account.Length is <1 or >150
            ||review.EvidencePage.Length is <1 or >300||!ReviewerName(review.Reviewer)||review.PublishedAt>DateTimeOffset.UtcNow
            ||review.PublishedAt>DateTimeOffset.Parse(document.FirstSeenAt,CultureInfo.InvariantCulture)
            ||review.HoldingAsOf>DateOnly.FromDateTime(review.PublishedAt.UtcDateTime))throw new InvalidDataException("具名持仓的主体、时序、报告期或证据位置无效。");
        var id=ExitAlertEvaluator.Hash(document.Id+"|"+review.LegalEntityId+"|"+review.Account+"|"+review.HoldingAsOf.ToString("yyyy-MM-dd"));
        var row=new NamedEtfHolding(id,document.Market,document.Ticker,review.LegalEntityId,review.LegalName,review.Account,review.HoldingAsOf,
            review.PublishedAt,DateTimeOffset.Parse(document.FirstSeenAt,CultureInfo.InvariantCulture),review.Shares,document.Url,document.Hash,review.EvidencePage,review.Reviewer,review.OriginalConfirmed,review.IndependentOwnershipConfirmed);
        if(!ExitAlertEvaluator.NamedVerified(row))throw new InvalidDataException("匿名机构、重叠持有关系或未经原件确认不能产生具名证据。");
        var payload=JsonSerializer.Serialize(row);var old=Scalar("SELECT payload FROM nt_named_etf_holding WHERE id=$id",("$id",id)) as string;
        if(old is not null&&old!=payload)throw new InvalidDataException("已确认持仓不能覆盖，修订需登记新的官方原件。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("INSERT OR IGNORE INTO nt_named_etf_holding VALUES($id,$m,$t,$p)",("$id",id),("$m",row.Market),("$t",row.Ticker),("$p",payload));
        MarkAlertPending();RecordCommand(commandId,"alert-holding");transaction.Commit();
    }
    public List<NamedEtfHolding> NamedEtfHoldings(EtfKey? key=null)
    {
        using var command=Command("SELECT payload FROM nt_named_etf_holding WHERE $m IS NULL OR (market=$m AND ticker=$t)",("$m",key?.Market),("$t",key?.Ticker));
        return ReadJson<NamedEtfHolding>(command).Select(x=>raw.Verify(x.DocumentHash)?x:x with{OriginalConfirmed=false}).ToList();
    }
    private static bool Identifier(string value)=>value.Length is >0 and <=80&&value.All(c=>!char.IsControl(c)&&c!='|');
    private static bool ReviewerName(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=120&&!value.Any(char.IsControl);
}
