using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class AlertStore
{
    /// <summary>当前证券、来源记录及Q15在同一只读快照绑定；读取不写入审核状态或产生通知。</summary>
    public EtfHolderDocument EtfHolderDocument(EtfKey key,string documentId)
    {
        if(documentId.Length!=64||!documentId.All(Uri.IsHexDigit))throw new InvalidDataException("ETF原件标识无效。");
        using var command=Command("""
            SELECT d.id,d.market,d.ticker,d.url,d.hash,d.first_seen FROM nt_alert_document d JOIN source_fetch f ON f.fetch_id=d.fetch_id
            WHERE d.id=$id AND d.market=$m AND d.ticker=$t AND f.sha256=d.hash AND f.original_sha256=d.hash
                AND f.provider='ReviewedEtfAlertPdf' AND f.status!='ParseFailed'
                AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
            """,("$id",documentId),("$m",key.Market),("$t",key.Ticker));
        using var reader=command.ExecuteReader();
        if(!reader.Read())throw new InvalidDataException("ETF原件未登记、证券不符或处于完整性隔离状态。");
        var document=new AlertDocument(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5));
        if(!DateTimeOffset.TryParse(document.FirstSeenAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var first)||first>clock.GetUtcNow())
            throw new InvalidDataException("ETF原件首见时刻无效。");
        return new(document,raw.ReadVerified(document.Hash,session.OperationCancellation));
    }
}
