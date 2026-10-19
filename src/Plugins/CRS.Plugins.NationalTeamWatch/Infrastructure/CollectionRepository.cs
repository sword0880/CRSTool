using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    /// <summary>每次请求独立建档，不覆盖同日旧失败；事务保证预期阶段同时落库。</summary>
    public string BeginCollection(EtfKey key,DateOnly day)
    {
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM security_master WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker)))!=1)
            throw new InvalidDataException("采集证券未登记。");
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();var id=Guid.NewGuid().ToString("N");
        Execute("INSERT INTO etf_collection_attempt VALUES($id,$m,$t,$d,$now)",("$id",id),("$m",key.Market),("$t",key.Ticker),("$d",day.ToString("yyyy-MM-dd")),("$now",Now));
        RecordCollection(id,"Expected",IsReviewedTradingDay(key.Market,day)==true?"ReviewedTradingDay":"CalendarUnverified");
        transaction.Commit();return id;
    }
    /// <summary>追加阶段记录；不覆盖失败历史，也不凭采集状态创建有效份额。</summary>
    public void RecordCollection(string id,string state,string reason,string? fetchId=null)
    {
        if(state is not ("Expected" or "Fetching" or "RawSaved" or "Parsed" or "Validating" or "Accepted" or "NotPublished" or "AccessBlocked" or "ParseFailed" or "Missing" or "NeedsReview" or "SourceFailed" or "Interrupted"))
            throw new InvalidDataException("未知采集阶段。");
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>100)throw new InvalidDataException("采集原因必须为简短状态码。");
        beforeWrite?.Invoke();
        Execute("INSERT INTO etf_collection_transition(attempt_id,state,reason,fetch_id,updated_at) VALUES($id,$s,$r,$f,$now)",("$id",id),("$s",state),("$r",reason),("$f",fetchId),("$now",Now));
    }
    /// <summary>保留全部尝试，每次尝试展示最新阶段；日期限制控制查询规模。</summary>
    public List<CollectionAttempt> CollectionAttempts(EtfKey key,DateOnly from,DateOnly to)
    {
        if(from>to||to.DayNumber-from.DayNumber>400)throw new InvalidDataException("采集查询区间必须不超过400个自然日。");
        using var command=Command("""
            SELECT a.id,a.trade_date,t.state,t.reason,t.fetch_id,t.updated_at,f.source_url,f.parser_version,f.original_sha256
            FROM etf_collection_attempt a JOIN etf_collection_transition t ON t.attempt_id=a.id
            LEFT JOIN source_fetch f ON f.fetch_id=t.fetch_id
            WHERE a.market=$m AND a.ticker=$code AND a.trade_date BETWEEN $from AND $to
            AND t.sequence=(SELECT max(sequence) FROM etf_collection_transition WHERE attempt_id=a.id)
            ORDER BY t.sequence DESC
            """,("$m",key.Market),("$code",key.Ticker),("$from",from.ToString("yyyy-MM-dd")),("$to",to.ToString("yyyy-MM-dd")));
        using var reader=command.ExecuteReader();var result=new List<CollectionAttempt>();
        while(reader.Read())result.Add(new(reader.GetString(0),key.Market,key.Ticker,DateOnly.ParseExact(reader.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetString(2),reader.GetString(3),
            reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8)));
        return result;
    }
}
