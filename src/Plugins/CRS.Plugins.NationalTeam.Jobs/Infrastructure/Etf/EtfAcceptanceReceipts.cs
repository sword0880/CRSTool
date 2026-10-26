using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private sealed record AcceptanceReceipt(string Hash,string AcceptedAt,string ReceivedAt);

    /// <summary>一次读取最近20个交易日的最新采集阶段和原始接收时点；不把阶段重放当作重新采集。</summary>
    private Dictionary<(EtfKey Key,DateOnly Day),List<AcceptanceReceipt>> ReadAcceptanceReceipts(
        IEnumerable<EtfKey> keys,IReadOnlyDictionary<string,(DateOnly Date,bool Valid)[]> calendars)
    {
        var result=new Dictionary<(EtfKey,DateOnly),List<AcceptanceReceipt>>();
        foreach(var batch in keys.Where(x=>calendars[x.Market].Length>0).Chunk(128))
        {
            operationCancellation.ThrowIfCancellationRequested();
            var parameters=new List<(string,object?)>();
            var targets=string.Join(',',batch.Select((key,index)=>
            {
                var days=calendars[key.Market].TakeLast(20).ToArray();
                parameters.Add(("$m"+index,key.Market));parameters.Add(("$t"+index,key.Ticker));
                parameters.Add(("$from"+index,days[0].Date.ToString("yyyy-MM-dd")));
                parameters.Add(("$to"+index,days[^1].Date.ToString("yyyy-MM-dd")));
                return "($m"+index+",$t"+index+",$from"+index+",$to"+index+")";
            }));
            var sql="WITH targets(market,ticker,first_day,last_day) AS (VALUES "+targets+") "+"""
                SELECT a.market,a.ticker,a.trade_date,f.original_sha256,t.updated_at,f.retrieved_utc
                FROM targets k JOIN etf_collection_attempt a ON a.market=k.market AND a.ticker=k.ticker
                    AND a.trade_date BETWEEN k.first_day AND k.last_day
                JOIN trading_day d ON d.market=a.market AND d.trade_date=a.trade_date
                JOIN etf_collection_transition t ON t.attempt_id=a.id
                JOIN source_fetch f ON f.fetch_id=t.fetch_id
                WHERE t.state='Accepted'
                    AND t.sequence=(SELECT max(sequence) FROM etf_collection_transition WHERE attempt_id=a.id)
                ORDER BY t.sequence DESC
                """;
            using var command=Command(sql,parameters.ToArray());using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                operationCancellation.ThrowIfCancellationRequested();
                var key=(new EtfKey(reader.GetString(0),reader.GetString(1)),DateOnly.ParseExact(reader.GetString(2),"yyyy-MM-dd",CultureInfo.InvariantCulture));
                if(!result.TryGetValue(key,out var receipts))result.Add(key,receipts=[]);
                receipts.Add(new(reader.GetString(3),reader.GetString(4),reader.GetString(5)));
            }
        }
        return result;
    }
}
