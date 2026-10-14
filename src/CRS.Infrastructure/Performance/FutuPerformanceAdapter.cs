using CRS.Domain.Performance;
using static CRS.Infrastructure.PerformanceBrokerMapping;
namespace CRS.Infrastructure;

/// <summary>富途读取共享解析事实后投影绩效；不调用税务计算或 FIFO 成本。</summary>
internal static class FutuPerformanceAdapter
{
    internal static Dictionary<string,string> AccountLabels(FrozenInput input)
    {
        using var table=new FutuTable(input);
        return AccountLabels(table.Read("账户信息",true,"年份","账户号码","账户名称")!);
    }
    private static Dictionary<string,string> AccountLabels(IEnumerable<FutuRow> rows)
        =>rows.Where(r=>r["账户号码"]!="").GroupBy(r=>r["账户号码"]).ToDictionary(g=>AccountId("FUTU","FUTU:"+g.Key),g=>"富途 "+g.First()["账户名称"]+"（"+g.Key+"）");
    internal static PerformanceData Read(FrozenInput input,CancellationToken cancellation)
    {
        using var table=new FutuTable(input);
        // 年度收入汇总不是现金事件，不能与流水重复计入或冒充完整净值资料。
        if(table.Read("交易流水",false) is null)throw new CrsException("绩效不将富途年度收入汇总转换为资金事件，请选择包含交易流水的年度工作簿。");
        var rows=table.Read("账户信息",true,"年份","账户号码","账户名称")!;var years=rows.Select(r=>FutuFields.Year(r["年份"])).Distinct().ToArray();
        if(years.Length!=1)throw new CrsException("富途年度工作簿必须包含唯一明确年度。");var year=years[0];
        var imported=new ImportData{Broker="FUTU"};_=new FutuTradeExcelImporter().Read(input,table,imported,year,cancellation);
        return Project(input,BrokerActivityFacts.Freeze(imported,input.Sha256,"FutuAnnualActivity.v1"),year,AccountLabels(rows));
    }
    private static PerformanceData Project(FrozenInput input,BrokerActivityFacts imported,int year,Dictionary<string,string> labels)
    {
        var ids=imported.Sources.Select(s=>s.Account).Distinct().ToDictionary(raw=>raw,raw=>AccountId("FUTU",raw));
        PerformanceSource Source(string row)=>new(input.Sha256,row);
        // 年度表缺可靠基础币种字段；HKD 只是待确认编辑初值，不用于解释原币资产。
        var accounts=ids.Select(k=>new InvestmentAccount(k.Value,"FUTU",labels.GetValueOrDefault(k.Value,"富途（"+k.Key.Replace("FUTU:","")+"）"),"HKD",new(year-1,12,31),BaseCurrencyConfirmed:false)).ToArray();var events=new List<InvestmentEvent>();var navs=new List<ValuationSnapshot>();
        foreach(var t in imported.Trades)events.Add(new("FUTU:"+t.Id,ids[t.Key.Account],t.Time,DateOnly.FromDateTime(t.Time.DateTime),t.Side=="BUY"?InvestmentEventKind.Buy:InvestmentEventKind.Sell,t.Side=="BUY"?-t.Gross:t.Gross,t.Key.Currency,Source("交易流水:"+t.Row),t.Key.Instrument,t.Key.Market,t.Quantity,t.Fee,Confirmed:true));
        foreach(var f in imported.FundMovements)
        {
            var type=f.Description.Contains("withholding tax",StringComparison.OrdinalIgnoreCase)?"WITHHOLDING TAX":f.Type;var kind=CashKind(type,f.Amount);var id=input.Sha256+":"+f.Sheet+":"+f.Row;
            events.Add(new(id,ids[f.Account],new(f.Date.ToDateTime(new(12,0)),TimeSpan.Zero),f.Date,kind,f.Amount,f.Currency,Source(f.Sheet+":"+f.Row),f.Symbol,Confirmed:kind is not (InvestmentEventKind.Deposit or InvestmentEventKind.Withdrawal or InvestmentEventKind.CashTransfer or InvestmentEventKind.Unknown)));
        }
        foreach(var move in imported.SecurityMovements)
            events.Add(new(input.Sha256+":"+move.Sheet+":"+move.Row,ids[move.Key.Account],End(move.Date),move.Date,InvestmentEventKind.Unsupported,0,move.Key.Currency.Length==3?move.Key.Currency:"HKD",Source(move.Sheet+":"+move.Row),move.Key.Instrument,move.Key.Market,move.Quantity));
        foreach(var issue in imported.Issues.Where(i=>i.AffectsCost || i.Code is "UNSUPPORTED_ASSET" or "MISSING_TRADE_FEES" or "TRADE_AMOUNT_MISMATCH" or "ZERO_QUANTITY"))
        {
            var account=ids.GetValueOrDefault(issue.Account,ids.Values.First());var date=issue.Date??new DateOnly(year,12,31);
            events.Add(new(input.Sha256+":Issue:"+events.Count,account,End(date),date,InvestmentEventKind.Unsupported,0,issue.Currency.Length==3?issue.Currency:"HKD",Source(issue.Code+":"+issue.RecordId),issue.Instrument));
        }
        foreach(var raw in ids.Keys)
        foreach(var date in new[]{new DateOnly(year-1,12,31),new DateOnly(year,12,31)})
        {
            var comps=new List<ValuationComponent>();var positions=(date.Year<year?imported.BeginningPositions:imported.Positions).Where(p=>p.Key.Account==raw).ToArray();
            foreach(var p in positions)comps.Add(new("Security",p.Key.Currency,null,p.Key.Instrument,p.Key.Market,Math.Abs(p.Quantity)));
            foreach(var cash in imported.CashChecks.Where(c=>c.Account==raw)) {var value=date.Year<year?cash.Starting:cash.ReportedEnding;comps.Add(new(value<0?"Liability":"Cash",cash.Currency,value is null?null:Math.Abs(value.Value)));}
            if(!comps.Any(c=>c.Kind is "Cash" or "Liability"))comps.Add(new("Cash","HKD",null));
            if(!imported.Sources.Any(s=>s.Account==raw && s.HasPositions))comps.Add(new("Security","HKD",null));
            navs.Add(new(input.Sha256+":"+ids[raw]+":"+date,ids[raw],End(date),date,ValuationMoment.EndOfDay,comps.ToArray(),Source("证券持仓/资金总览"),PerformanceQuality.Reconstructed));
        }
        // 原件不含市值的持仓留空，用户可离线补行情；年度收入主表不再叠加到现金事件。
        return new(){Accounts=accounts,Events=events.Select(e=>e with{Quality=PerformanceQuality.Reconstructed}).ToArray(),Valuations=navs.ToArray(),Coverage=imported.Sources.Select(s=>new PerformanceCoverage(ids[s.Account],s.Start,s.End,s.HasCash && imported.CashChecks.Where(c=>c.Account==s.Account).Any() && imported.CashChecks.Where(c=>c.Account==s.Account).All(c=>c.Status=="一致"),Source("资金进出/资金总览"))).ToArray()};
    }
}
