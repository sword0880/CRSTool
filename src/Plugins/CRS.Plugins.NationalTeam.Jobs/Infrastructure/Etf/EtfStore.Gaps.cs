using System.Globalization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EtfStore
{
    /// <summary>分段扫描所有已登记年份，优先最近250交易日，并让未尝试的缺口先行。</summary>
    public List<ShareGap> DueHistoricalShareGaps(DateOnly through,DateTimeOffset now)
    {
        var first=Scalar("SELECT min(trade_date) FROM trading_day") as string;
        if(first is null)return [];
        var gaps=new List<ShareGap>();var start=DateOnly.ParseExact(first,"yyyy-MM-dd",CultureInfo.InvariantCulture);
        while(start<=through)
        {
            var end=through.DayNumber-start.DayNumber>400?start.AddDays(400):through;
            gaps.AddRange(DueShareGaps(start,end,now));
            if(end==through)break;start=end.AddDays(1);
        }
        var targets=Configuration().Where(x=>x.Active).Select(x=>x.Market).Distinct().ToDictionary(x=>x,x=>ReadCalendar(x).Where(d=>d.Valid&&d.Date<=through).TakeLast(250).FirstOrDefault().Date);
        return gaps.OrderBy(x=>x.Date<targets[x.Key.Market]?1:0).ThenBy(x=>x.LastAttempt??DateTimeOffset.MinValue).ThenBy(x=>x.Date).ThenBy(x=>x.Key.Market).ThenBy(x=>x.Key.Ticker).ToList();
    }
    /// <summary>只从已核验日历识别缺口；失败重试有持久冷却，不以查询游标当作完整性证据。</summary>
    public List<ShareGap> DueShareGaps(DateOnly from,DateOnly to,DateTimeOffset now)
    {
        if(from>to||to.DayNumber-from.DayNumber>400)throw new InvalidDataException("缺口区间无效。");
        ValidateEvidence();var facts=ReadEvidence();var result=new List<ShareGap>();
        foreach(var item in Configuration().Where(x=>x.Active))
        {
            var key=new EtfKey(item.Market,item.Ticker);
            var accepted=ReadPoints(key).Select(x=>x.Date).ToHashSet();
            var pending=facts.Where(x=>x.Market==key.Market&&x.Ticker==key.Ticker&&x.State=="NeedsReview").Select(x=>DateOnly.Parse(x.TradeDate,CultureInfo.InvariantCulture)).ToHashSet();
            var attempts=CollectionAttempts(key,from,to).GroupBy(x=>x.Date).ToDictionary(x=>x.Key,x=>x.ToArray());
            foreach(var day in ReadCalendar(key.Market).Where(x=>x.Valid&&x.Date>=from&&x.Date<=to).Select(x=>x.Date))
            {
                if(accepted.Contains(day)||pending.Contains(day))continue;
                attempts.TryGetValue(day,out var history);var last=history?.FirstOrDefault();
                var time=last is null?(DateTimeOffset?)null:DateTimeOffset.Parse(last.UpdatedAt,CultureInfo.InvariantCulture);
                // 来源限制和解析异常均跨重启冷却，不能靠重复任务绕过来源退避。
                var delay=last?.State switch
                {
                    "AccessBlocked"=>TimeSpan.FromHours(6),
                    "NotPublished" or "ParseFailed"=>TimeSpan.FromDays(1),
                    "Fetching" or "RawSaved" or "Parsed" or "Validating"=>TimeSpan.FromMinutes(20),
                    "Interrupted"=>TimeSpan.Zero,
                    _=>TimeSpan.FromMinutes(Math.Min(360,5*Math.Pow(2,Math.Min(7,(history?.Length??1)-1))))
                };
                if(time is null||time+delay<=now)result.Add(new(key,day,time));
            }
        }
        // 没尝试过的缺口先行；同一长期不发布日期不会永远挡住其它日期。
        return result.OrderBy(x=>x.LastAttempt??DateTimeOffset.MinValue).ThenBy(x=>x.Date).ThenBy(x=>x.Key.Market).ThenBy(x=>x.Key.Ticker).ToList();
    }
}
