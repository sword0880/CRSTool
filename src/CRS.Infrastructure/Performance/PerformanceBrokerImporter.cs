using System.Globalization;
using System.Text;
using System.Xml.Linq;
using CRS.Domain.Performance;
using static CRS.Infrastructure.XmlFields;

namespace CRS.Infrastructure;

/// <summary>仅映射实际结单字段；现金汇总不生成事件，缺净值/价时/流量证据不补零。</summary>
internal static class PerformanceBrokerImporter
{
    internal static Dictionary<string,string> FutuAccountLabels(FrozenInput input)
    {
        using var table=new FutuTable(input);
        return FutuAccountLabels(table.Read("账户信息",true,"年份","账户号码","账户名称")!);
    }
    private static Dictionary<string,string> FutuAccountLabels(IEnumerable<FutuRow> rows)
        =>rows.Where(r=>r["账户号码"]!="").GroupBy(r=>r["账户号码"]).ToDictionary(g=>AccountId("FUTU","FUTU:"+g.Key),g=>"富途 "+g.First()["账户名称"]+"（"+g.Key+"）");
    private static string AccountId(string broker,string raw)=>broker+":"+PerformanceCodec.Hash(Encoding.UTF8.GetBytes(raw))[..24];
    private static DateTimeOffset End(DateOnly day)=>new(day.ToDateTime(new(23,59,59)),TimeSpan.Zero);
    private static InvestmentEventKind CashKind(string type,decimal amount)=>type.Trim().ToUpperInvariant() switch
    {
        "DEPOSITS/WITHDRAWALS" or "入金" or "出金" or "存入资金" or "提取资金"=>amount>=0?InvestmentEventKind.Deposit:InvestmentEventKind.Withdrawal,
        "DIVIDENDS" or "PAYMENT IN LIEU OF DIVIDENDS" or "现金股息" or "派息" or "股息"=>InvestmentEventKind.Dividend,
        "BROKER INTEREST RECEIVED" or "利息"=>InvestmentEventKind.Interest,
        "BROKER INTEREST PAID" or "融资利息"=>InvestmentEventKind.FinancingInterest,
        "WITHHOLDING TAX" or "预扣税"=>InvestmentEventKind.WithholdingTax,
        "OTHER FEES" or "费用" or "手续费"=>InvestmentEventKind.Fee,
        "INTERNAL TRANSFER" or "INTERNAL TRANSFERS" or "TRANSFER" or "转账"=>InvestmentEventKind.CashTransfer,
        "换汇" or "兑换"=>InvestmentEventKind.FxExchange,
        _=>InvestmentEventKind.Unknown
    };
    internal static PerformanceData Ibkr(FrozenInput input,CancellationToken cancellation)
    {
        using var stream=input.OpenRead();var root=ReadXml(stream).Root;
        if(root?.Name.LocalName!="FlexQueryResponse" || Optional(root,"type","AF")!="AF")throw new CrsException("绩效仅接受 IBKR Activity Flex XML。");
        var accounts=new List<InvestmentAccount>();var events=new List<InvestmentEvent>();var navs=new List<ValuationSnapshot>();var prices=new List<SecurityPrice>();var fx=new List<PerformanceFx>();var coverage=new List<PerformanceCoverage>();
        var statements=root.Element("FlexStatements")?.Elements("FlexStatement").ToArray()??[];if(statements.Length==0)throw new CrsException("没有 IBKR 活动结单。");
        foreach(var s in statements)
        {
            cancellation.ThrowIfCancellationRequested();var raw=Required(s,"accountId");var account=AccountId("IBKR",raw);var start=Date(Required(s,"fromDate"));var end=Date(Required(s,"toDate"));
            // 缺少基础币种时拒绝转换，避免凭美元假定解释 fxRateToBase。
            var reportedCurrency=s.Element("AccountInformation") is { } info?Optional(info,"currency"):"";
            var baseCurrency=reportedCurrency.Length==3?reportedCurrency:"USD";
            if(start>end)throw new CrsException("IBKR 结单期间无效。");
            accounts.Add(new(account,"IBKR","IBKR 账户 "+account[^6..],baseCurrency,start,BaseCurrencyConfirmed:reportedCurrency.Length==3));
            PerformanceSource Source(string row)=>new(input.Sha256,row);
            void Fx(XElement e,DateOnly date,string row)
            {
                var currency=Optional(e,"currency");if(reportedCurrency.Length!=3 || currency=="" || currency==baseCurrency || Optional(e,"fxRateToBase")=="")return;
                fx.Add(new(input.Sha256+":"+row+":FX",currency,baseCurrency,date,Number(e,"fxRateToBase"),Source(row),PerformanceQuality.Reconstructed));
            }
            var rowIndex=0;
            foreach(var e in s.Element("Trades")?.Elements("Trade")??[])
            {
                cancellation.ThrowIfCancellationRequested();rowIndex++;if(Optional(e,"levelOfDetail")!="EXECUTION")continue;
                if(Optional(e,"accountId",raw)!=raw)throw new CrsException("成交与结单账户不一致。");
                var row="Trades:"+rowIndex;var time=Time(Required(e,"dateTime"));var day=Optional(e,"tradeDate")==""?DateOnly.FromDateTime(time.Value.DateTime):Date(Required(e,"tradeDate"));
                var asset=Required(e,"assetCategory");var currency=Required(e,"currency");var quantity=Math.Abs(Number(e,"quantity"));var fee=Math.Abs(Number(e,"ibCommission"))+Math.Abs(Number(e,"taxes"));
                var id=account+":Trade:"+Required(e,"tradeID");var kind=asset is "STK" or "ETF"?Required(e,"buySell")=="BUY"?InvestmentEventKind.Buy:InvestmentEventKind.Sell:asset=="CASH"?InvestmentEventKind.FxExchange:InvestmentEventKind.Unsupported;
                if(Optional(e,"origTradeID") is not ("" or "0") || Required(e,"ibCommissionCurrency")!=currency)kind=InvestmentEventKind.Unsupported;
                events.Add(new(id,account,time.Value,day,kind,Number(e,"proceeds"),currency,Source(row),Optional(e,"conid",Optional(e,"symbol")),Optional(e,"listingExchange",Optional(e,"exchange","UNKNOWN")),quantity,fee,Reference:Optional(e,"transactionID"),Confirmed:true));
                Fx(e,day,row);
            }
            rowIndex=0;
            foreach(var e in s.Element("CashTransactions")?.Elements("CashTransaction")??[])
            {
                cancellation.ThrowIfCancellationRequested();rowIndex++;if(Optional(e,"levelOfDetail","DETAIL")!="DETAIL")continue;
                if(Optional(e,"accountId",raw)!=raw)throw new CrsException("现金事件与结单账户不一致。");
                var row="CashTransactions:"+rowIndex;var time=Time(Required(e,"dateTime"));var day=DateOnly.FromDateTime(time.Value.DateTime);var amount=Number(e,"amount");var kind=CashKind(Required(e,"type"),amount);
                // Deposits/Withdrawals 没有证明另一侧账户归属，首次导入保留确认门槛。
                events.Add(new(account+":Cash:"+Required(e,"transactionID"),account,time.Value,day,kind,amount,Required(e,"currency"),Source(row),Optional(e,"conid"),Optional(e,"listingExchange"),Reference:Optional(e,"clientReference"),Confirmed:kind is not (InvestmentEventKind.Deposit or InvestmentEventKind.Withdrawal or InvestmentEventKind.CashTransfer or InvestmentEventKind.Unknown)));
                Fx(e,day,row);
            }
            var components=new List<ValuationComponent>();var positionSection=s.Element("OpenPositions");rowIndex=0;
            foreach(var e in positionSection?.Elements().Where(e=>Optional(e,"levelOfDetail")=="SUMMARY")??[])
            {
                rowIndex++;var row="OpenPositions:"+rowIndex;var currency=Required(e,"currency");var quantity=Number(e,"position");var security=Required(e,"conid");var market=Optional(e,"listingExchange","UNKNOWN");
                components.Add(new(quantity<0?"Liability":"Security",currency,quantity<0?Math.Abs(Number(e,"positionValue")):Number(e,"positionValue"),security,market,Math.Abs(quantity)));
                if(quantity<0 || Optional(e,"assetCategory") is not ("STK" or "ETF"))events.Add(new(input.Sha256+":"+row+":Unsupported",account,End(end),end,InvestmentEventKind.Unsupported,0,currency,Source(row),security,market));
                if(quantity>=0)prices.Add(new(input.Sha256+":"+row,security,market,currency,end,Number(e,"markPrice"),Source(row),PerformanceQuality.Reconstructed));
                Fx(e,end,row);
            }
            if(positionSection is null)components.Add(new("Security",baseCurrency,null));
            var cashRows=s.Element("CashReport")?.Elements().Where(e=>Optional(e,"levelOfDetail")=="Currency").ToArray()??[];
            if(cashRows.Length==0)components.Add(new("Cash",baseCurrency,null));
            foreach(var e in cashRows)
            {
                var cash=Number(e,"endingCash");components.Add(new(cash<0?"Liability":"Cash",Required(e,"currency"),Math.Abs(cash)));
            }
            navs.Add(new(input.Sha256+":"+account+":End",account,End(end),end,ValuationMoment.EndOfDay,components.ToArray(),Source("OpenPositions+CashReport"),PerformanceQuality.Reconstructed));
            // 实际样本没有期初完整证券 NAV，现金期初余额不能冒充期初净资产。
            var complete=s.Element("CashTransactions") is not null && cashRows.Length>0;
            foreach(var e in cashRows)
            {
                var expected=Optional(e,"depositWithdrawals");var actual=events.Where(x=>x.AccountId==account && x.Date>=start && x.Date<=end && x.Currency==Required(e,"currency") && x.Kind is InvestmentEventKind.Deposit or InvestmentEventKind.Withdrawal).Sum(x=>x.Amount);
                if(expected=="" || Math.Abs(ParseNumber(expected,"depositWithdrawals")-actual)>.02m)complete=false;
                if(new[]{"accountTransfers","internalTransfers"}.Any(field=>Optional(e,field)!="" && Number(e,field)!=0)
                    && !events.Any(x=>x.AccountId==account && x.Kind==InvestmentEventKind.CashTransfer))complete=false;
            }
            var cashChecked=CashBalanceChecker.Check(s,raw,start,end,input.Name).All(c=>c.Status=="一致");
            coverage.Add(new(account,start,end,complete,Source("CashTransactions/CashReport"),IncomeComplete:cashChecked,CostsComplete:cashChecked));
            foreach(var section in s.Elements().Where(x=>x.Name.LocalName is "Transfers" or "TradeTransfers" or "CorporateActions" or "OptionEAE" or "Adjustments" or "SLBActivities" or "SLBFees" or "CFDCharges" or "UnsettledTransfers" or "StockGrantActivities" or "UnbookedTrades" or "TransactionTaxes"))
            foreach(var e in section.Elements())
            {
                var date=Optional(e,"reportDate")==""?end:Date(Required(e,"reportDate"));var row=section.Name.LocalName+":"+(++rowIndex);
                // 缺可靠实物市值/拆股比例时保留待核对，不用数量或成本代替外部转仓公允价值。
                events.Add(new(input.Sha256+":"+row,account,End(date),date,InvestmentEventKind.Unsupported,0,Optional(e,"currency",baseCurrency),Source(row),Optional(e,"conid"),Optional(e,"listingExchange"),Reference:Optional(e,"transactionID")));
            }
        }
        return new(){Accounts=accounts.GroupBy(a=>a.Id).Select(g=>g.First() with {TrustedFrom=g.Min(a=>a.TrustedFrom)}).ToArray(),Events=events.Select(e=>e with{Quality=PerformanceQuality.Reconstructed}).ToArray(),Valuations=navs.ToArray(),Prices=prices.ToArray(),Fx=fx.ToArray(),Coverage=coverage.ToArray()};
    }
    internal static PerformanceData Futu(FrozenInput input,CancellationToken cancellation)
    {
        using var table=new FutuTable(input);
        // 年度收入汇总不是现金事件，不能与流水重复计入或冒充完整净值资料。
        if(table.Read("交易流水",false) is null)throw new CrsException("绩效不将富途年度收入汇总转换为资金事件，请选择包含交易流水的年度工作簿。");
        var rows=table.Read("账户信息",true,"年份","账户号码","账户名称")!;var years=rows.Select(r=>FutuFields.Year(r["年份"])).Distinct().ToArray();
        if(years.Length!=1)throw new CrsException("富途年度工作簿必须包含唯一明确年度。");var year=years[0];
        var imported=new ImportData{Broker="FUTU"};_=new FutuTradeExcelImporter().Read(input,table,imported,year,cancellation);
        var ids=imported.Sources.Select(s=>s.Account).Distinct().ToDictionary(raw=>raw,raw=>AccountId("FUTU",raw));
        PerformanceSource Source(string row)=>new(input.Sha256,row);
        // 年度表缺可靠基础币种字段；HKD 只是待确认编辑初值，不用于解释原币资产。
        var labels=FutuAccountLabels(rows);
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
