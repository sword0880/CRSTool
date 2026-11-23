namespace CRS.Domain.Performance;

internal sealed record PerformanceCostResult(decimal? Realized,decimal? Unrealized,string Explanation);
/// <summary>独立区间市场成本：期初持仓按期初公允价值建仓，交易费用入成本；不使用税务 LOT。</summary>
internal static class PerformanceCostCalculator
{
    private sealed record Lot(decimal Quantity,decimal Cost);
    public static PerformanceCostResult Calculate(PerformanceData data,InvestmentEvent[] events,string[] accounts,DateOnly from,DateOnly to,
        Func<decimal,string,DateOnly,decimal?> convert,CancellationToken cancellation)
    {
        var lots=new Dictionary<(string Account,string Security,string Market,string Currency),List<Lot>>(); decimal realized=0,unrealized=0;
        const string definition="IntervalMarketFIFO.v1：期初持仓以期初市值为区间成本，买入含费用，卖出减费用；独立于税务 FIFO，不是成立以来盈亏或完整汇率归因。";
        PerformanceCostResult Missing()=>new(null,null,"区间持仓数量、证券估值、成交或转仓成本链不完整；不复用税务损益。");
        decimal? Market(ValuationComponent c,DateOnly date)
        {
            if(c.Amount is { } amount)return convert(amount,c.Currency,date);
            var price=data.Prices.Where(p=>p.Security==c.Security && p.Market==c.Market && p.Currency==c.Currency && p.Date==date).ToArray();
            // 原始持仓数量只搭配未复权价格，不能混用复权价格和实际拆股数量。
            return price.Length>0 && price.All(p=>p.Price==price[0].Price && !p.Adjusted && p.Quality!=PerformanceQuality.InsufficientData)?convert(price[0].Price*c.Quantity,c.Currency,date):null;
        }
        foreach(var account in accounts)
        {
            var opening=data.Valuations.FirstOrDefault(v=>v.AccountId==account && v.Date==from && v.Moment==ValuationMoment.EndOfDay);
            if(opening is null)return Missing();
            foreach(var c in opening.Components.Where(c=>c.Kind=="Security"))
            {
                var cost=Market(c,from); if(cost is null || c.Quantity<=0 || string.IsNullOrWhiteSpace(c.Security) || string.IsNullOrWhiteSpace(c.Market))return Missing();
                var key=(account,c.Security,c.Market,c.Currency); if(!lots.TryAdd(key,[new(c.Quantity,cost.Value)]))return Missing();
            }
        }
        var handled=new HashSet<string>();
        foreach(var e in events)
        {
            cancellation.ThrowIfCancellationRequested(); var key=(e.AccountId,e.Security,e.Market,e.Currency);
            if(e.Kind is InvestmentEventKind.Unknown or InvestmentEventKind.Unsupported)return Missing();
            if(e.Kind is not (InvestmentEventKind.Buy or InvestmentEventKind.Sell or InvestmentEventKind.Split or InvestmentEventKind.SecurityTransfer))continue;
            if(e.Security=="" || e.Market=="")return Missing();
            if(!lots.TryGetValue(key,out var queue))lots[key]=queue=[];
            if(e.Kind==InvestmentEventKind.Split)
            {if(e.SplitRatio<=0)return Missing();for(var i=0;i<queue.Count;i++)queue[i]=queue[i] with {Quantity=queue[i].Quantity*e.SplitRatio};continue;}
            if(e.Kind==InvestmentEventKind.Buy)
            {var cost=convert(Math.Abs(e.Amount)+e.Fees,e.Currency,e.Date);if(cost is null)return Missing();queue.Add(new(e.Quantity,cost.Value));continue;}
            if(e.Kind==InvestmentEventKind.SecurityTransfer && e.Amount>0)
            {
                if(handled.Contains(e.Id))continue;
                var link=data.Transfers.FirstOrDefault(l=>l.Confirmed && l.InEventId==e.Id && events.Any(x=>x.Id==l.OutEventId));
                if(link is not null)continue; // 内部转仓由转出端搬运原成本，不能再次以市值建仓。
                var cost=convert(e.Amount,e.Currency,e.Date); if(cost is null || e.Quantity==0)return Missing();queue.Add(new(Math.Abs(e.Quantity),cost.Value));continue;
            }
            var remaining=Math.Abs(e.Quantity);if(remaining==0)return Missing();decimal consumed=0;
            while(remaining>0 && queue.Count>0)
            {
                var lot=queue[0];var amount=Math.Min(remaining,lot.Quantity);var cost=lot.Cost*amount/lot.Quantity; consumed+=cost;remaining-=amount;
                if(amount==lot.Quantity)queue.RemoveAt(0);else queue[0]=new(lot.Quantity-amount,lot.Cost-cost);
            }
            if(remaining>0)return Missing();
            if(e.Kind==InvestmentEventKind.Sell)
            {var proceeds=convert(e.Amount-e.Fees,e.Currency,e.Date);if(proceeds is null)return Missing();realized+=proceeds.Value-consumed;}
            else
            {
                var link=data.Transfers.FirstOrDefault(l=>l.Confirmed && l.OutEventId==e.Id);var destination=events.FirstOrDefault(x=>x.Id==link?.InEventId);
                if(destination is not null)
                {
                    var target=(destination.AccountId,destination.Security,destination.Market,destination.Currency);
                    if(!lots.TryGetValue(target,out var targetLots))lots[target]=targetLots=[];
                    targetLots.Add(new(Math.Abs(destination.Quantity),consumed));handled.Add(destination.Id);
                }
            }
        }
        var endingKeys=new HashSet<(string Account,string Security,string Market,string Currency)>();
        foreach(var account in accounts)
        {
            var end=data.Valuations.FirstOrDefault(v=>v.AccountId==account && v.Date==to && v.Moment==ValuationMoment.EndOfDay);if(end is null)return Missing();
            foreach(var c in end.Components.Where(c=>c.Kind=="Security"))
            {
                var key=(account,c.Security,c.Market,c.Currency);if(!endingKeys.Add(key))return Missing();
                var remaining=lots.GetValueOrDefault(key,[]);var value=Market(c,to);
                if(value is null || remaining.Sum(l=>l.Quantity)!=c.Quantity)return Missing();unrealized+=value.Value-remaining.Sum(l=>l.Cost);
            }
        }
        if(lots.Any(k=>k.Value.Sum(l=>l.Quantity)>0 && !endingKeys.Contains(k.Key)))return Missing();
        return new(realized,unrealized,definition);
    }
}
