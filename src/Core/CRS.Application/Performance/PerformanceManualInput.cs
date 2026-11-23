// 人工业绩补录入口：要求账户、来源及完整性确认，再转换为统一导入文档；缺少流水不能视为零流量，估值必须先确认账户基础币种。
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Domain.Performance;

namespace CRS.Application.Performance;

public sealed record ManualPerformanceAccount(string Broker,string Alias,string Currency,DateOnly From);
public sealed record ManualPerformanceEvent(string? AccountId,DateOnly Date,InvestmentEventKind Kind,decimal Amount,string Currency,string Source,
    string Security,string Market,decimal Quantity,decimal Fee,decimal Withholding,bool Confirmed);
public sealed record ManualPerformanceValuation(string? AccountId,DateOnly Date,decimal Securities,decimal Cash,decimal Receivable,
    decimal Liability,decimal Payable,bool Complete,string Source);
public sealed record ManualPerformanceCoverage(string? AccountId,DateOnly From,DateOnly To,bool FlowsComplete,bool IncomeCostsComplete,string Source);

public sealed partial class PerformanceUseCases
{
    public Task<bool> AddAccountAsync(ManualPerformanceAccount input,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {
        if(string.IsNullOrWhiteSpace(input.Alias))throw new CrsException("请填写境外账户别名。");
        return ImportManualData(new(){Accounts=[new(Guid.NewGuid().ToString("N"),input.Broker,input.Alias,input.Currency,input.From)]},token);
    });

    public Task<bool> AddEventAsync(ManualPerformanceEvent input,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {
        var account=RequireManualAccount(input.AccountId,input.Source,"资金/证券事件必须选择账户并填写原始来源。");
        return ImportManualData(new(){Events=[new(Guid.NewGuid().ToString("N"),account.Id,
            CRS.Timing.BeijingClock.At(input.Date,new(12,0)),input.Date,input.Kind,input.Amount,input.Currency,new("",input.Source),
            input.Security,input.Market,input.Quantity,input.Fee,input.Withholding,Confirmed:input.Confirmed)]},token);
    });

    // 估值补录先确认账户基础币种和当日资产负债完整性，再把资产、应收和负债转换为统一估值分量。
    public Task<bool> AddValuationAsync(ManualPerformanceValuation input,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {
        var account=RequireManualAccount(input.AccountId,input.Source,"请选择账户，填写来源，并明确确认本日资产/负债完整。");
        if(!input.Complete)throw new CrsException("请选择账户，填写来源，并明确确认本日资产/负债完整。");
        if(!account.BaseCurrencyConfirmed)throw new CrsException("请先核对并确认该账户基础币种，不能按预设币种补录整体净值。");
        if(input.Securities<0 || input.Cash<0 || input.Receivable<0 || input.Liability<0 || input.Payable<0)
            throw new CrsException("资产、应收和负债金额不能为负；负现金请填融资负债。");
        var currency=account.BaseCurrency;
        var components=new List<ValuationComponent>{new("Cash",currency,input.Cash),new("Receivable",currency,input.Receivable),
            new("Liability",currency,input.Liability),new("Payable",currency,input.Payable)};
        if(input.Securities>0)components.Add(new("Security",currency,input.Securities));
        return ImportManualData(new(){Valuations=[new(Guid.NewGuid().ToString("N"),account.Id,
            CRS.Timing.BeijingClock.At(input.Date,new(23,59,59)),input.Date,ValuationMoment.EndOfDay,components.ToArray(),
            new("",input.Source),PerformanceQuality.Estimated)]},token);
    });

    // 完整性来自账户、来源与明确确认；没有确认的流水区间不能用零资金流参与收益计算。
    public Task<bool> ConfirmCoverageAsync(ManualPerformanceCoverage input,CancellationToken cancellation=default)=>Run(cancellation,token=>
    {
        var account=RequireManualAccount(input.AccountId,input.Source,"完整性确认需要账户、来源和明确勾选，不能将缺失资金流水视为零。");
        if(!input.FlowsComplete)throw new CrsException("完整性确认需要账户、来源和明确勾选，不能将缺失资金流水视为零。");
        return ImportManualData(new(){Coverage=[new(account.Id,input.From,input.To,true,new("",input.Source),
            IncomeComplete:input.IncomeCostsComplete,CostsComplete:input.IncomeCostsComplete)]},token);
    });

    private InvestmentAccount RequireManualAccount(string? id,string source,string message)
    {
        if(string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source))throw new CrsException(message);
        return store.Load().Data.Accounts.SingleOrDefault(x=>x.Id==id)??throw new CrsException("账户已不存在，请重新加载后选择账户。");
    }

    private bool ImportManualData(PerformanceData data,CancellationToken token)
    {
        var json=JsonSerializer.Serialize(data,new JsonSerializerOptions{Converters={new JsonStringEnumConverter()}});
        var document=files.ReadManual(json);
        token.ThrowIfCancellationRequested();
        return store.Import(document);
    }
}
