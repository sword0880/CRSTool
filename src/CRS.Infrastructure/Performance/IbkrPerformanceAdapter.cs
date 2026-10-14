using System.Xml.Linq;
using CRS.Domain.Performance;
using static CRS.Infrastructure.XmlFields;
using static CRS.Infrastructure.PerformanceBrokerMapping;
namespace CRS.Infrastructure;

/// <summary>IBKR 格式解析与绩效投影；来源身份和既有映射规则保持不变。</summary>
internal static class IbkrPerformanceAdapter
{
    internal static PerformanceData Read(FrozenInput input,CancellationToken cancellation)
    {
        using var stream=input.OpenRead();var root=ReadXml(stream).Root;
        if(root?.Name.LocalName!="FlexQueryResponse" || Optional(root,"type","AF")!="AF")throw new CrsException("绩效仅接受 IBKR Activity Flex XML。");
        var statements=root.Element("FlexStatements")?.Elements("FlexStatement").ToArray()??[];if(statements.Length==0)throw new CrsException("没有 IBKR 活动结单。");
        return Project(input,statements,cancellation);
    }
    private static PerformanceData Project(FrozenInput input,XElement[] statements,CancellationToken cancellation)
    {
        var accounts=new List<InvestmentAccount>();var events=new List<InvestmentEvent>();var navs=new List<ValuationSnapshot>();var prices=new List<SecurityPrice>();var fx=new List<PerformanceFx>();var coverage=new List<PerformanceCoverage>();
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
}
