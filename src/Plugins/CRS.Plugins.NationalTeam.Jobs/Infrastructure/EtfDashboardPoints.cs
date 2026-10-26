using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>正式仪表盘沿外层同一只读事务分批读取；参数分组只限制SQL大小，不截断证券或事实。</summary>
    private Dictionary<EtfKey,List<SharePoint>> ReadDashboardPoints(IEnumerable<EtfKey> keys)
    {
        var requested=keys.ToArray();var result=requested.ToDictionary(x=>x,_=>new List<SharePoint>());
        // 包括无资料证券的空列表，防止按有数据的证券缩小覆盖分母；同代码跨市场仍为两个身份。
        foreach(var batch in requested.Chunk(128))
        {
            operationCancellation.ThrowIfCancellationRequested();
            var parameters=new List<(string,object?)>();
            var targets=string.Join(',',batch.Select((key,index)=>
            {parameters.Add(("$m"+index,key.Market));parameters.Add(("$t"+index,key.Ticker));return "($m"+index+",$t"+index+")";}));
            // 所有值仍走绑定参数；保留来源连接、Q15及未审修订阻断，与逐证券ReadPoints口径完全相同。
            var sql="WITH targets(market,ticker) AS (VALUES "+targets+") " + """
                SELECT c.market,c.ticker,r.trade_date,r.total_shares,r.revision_id
                FROM targets k JOIN share_accepted_current c ON c.market=k.market AND c.ticker=k.ticker
                JOIN share_observation_revision r ON r.revision_id=c.revision_id JOIN source_fetch f ON f.fetch_id=r.fetch_id
                WHERE NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                AND NOT EXISTS(SELECT 1 FROM share_observation_revision pending WHERE pending.market=c.market AND pending.ticker=c.ticker AND pending.trade_date=c.trade_date AND pending.status='NeedsReview')
                ORDER BY c.market,c.ticker,c.trade_date
                """;
            using var command=Command(sql,parameters.ToArray());using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                operationCancellation.ThrowIfCancellationRequested();var key=new EtfKey(reader.GetString(0),reader.GetString(1));
                result[key].Add(new(DateOnly.ParseExact(reader.GetString(2),"yyyy-MM-dd",CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(3),CultureInfo.InvariantCulture),null,reader.GetString(4)));
            }
        }
        return result;
    }
}
