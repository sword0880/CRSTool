using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace CRS.Domain.Performance;

/// <summary>单次计算私有副本及只读索引；不缓存跨快照的价格、汇率或分类。</summary>
internal sealed class PerformanceCalculationContext
{
    internal PerformanceData Data {get;}
    internal PerformanceRequest Request {get;}
    internal CancellationToken Cancellation {get;}
    internal IReadOnlyDictionary<string,InvestmentAccount> Accounts {get;}
    internal ILookup<(string Account,DateOnly Date,ValuationMoment Moment),ValuationSnapshot> Valuations {get;}
    internal ILookup<string,ValuationSnapshot> AccountValuations {get;}
    internal ILookup<(string Security,string Market,string Currency),SecurityPrice> Prices {get;}
    internal ILookup<(string From,string To,DateOnly Date),PerformanceFx> Fx {get;}
    internal ILookup<string,PerformanceCoverage> Coverage {get;}
    internal ILookup<(string Account,string Security),InvestmentEvent> SecurityEvents {get;}
    internal FrozenDictionary<string,InvestmentEvent> EffectiveEvents {get;}
    internal ILookup<string,TransferLink> ConfirmedTransfers {get;}

    internal PerformanceCalculationContext(PerformanceData data,PerformanceRequest request,CancellationToken cancellation)
    {
        // 模型为兼容历史 JSON 继续保留数组；运算只使用深复制后的集合。
        Data=PerformanceDataSnapshot.Copy(data);
        Request=request with{AccountIds=[..request.AccountIds]};Cancellation=cancellation;
        var byId=Data.Accounts.ToFrozenDictionary(a=>a.Id);
        Accounts=new ReadOnlyDictionary<string,InvestmentAccount>(Request.AccountIds.Order(StringComparer.Ordinal).ToDictionary(id=>id,id=>byId[id]));
        Valuations=Data.Valuations.ToLookup(v=>(v.AccountId,v.Date,v.Moment));
        AccountValuations=Data.Valuations.ToLookup(v=>v.AccountId);
        Prices=Data.Prices.ToLookup(p=>(p.Security,p.Market,p.Currency));
        Fx=Data.Fx.ToLookup(f=>(f.From,f.To,f.Date));
        Coverage=Data.Coverage.ToLookup(c=>c.AccountId);
        SecurityEvents=Data.Events.ToLookup(e=>(e.AccountId,e.Security));
        EffectiveEvents=PerformanceClassifications.Apply(Data).ToFrozenDictionary(e=>e.Id);
        ConfirmedTransfers=Data.Transfers.Where(l=>l.Confirmed).SelectMany(l=>new[]{(l.OutEventId,Link:l),(l.InEventId,Link:l)}).ToLookup(x=>x.Item1,x=>x.Link);
    }
}
