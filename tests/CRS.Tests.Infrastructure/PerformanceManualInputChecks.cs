using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain;
using CRS.Domain.Performance;
using CRS.Infrastructure;

internal static class PerformanceManualInputChecks
{
    internal static async Task RunAsync(string root)
    {
        using var vault=new VaultManager();
        await vault.CreateAsync(Path.Combine(root,"manual-input"),"Synthetic-Manual-Password-2026");await TestStores.Enroll(vault);
        var cases=vault.CreatePerformanceUseCases();var day=new DateOnly(2025,6,30);
        await cases.AddAccountAsync(new("IBKR","手工合成账户","USD",day));
        var account=(await cases.LoadAsync()).Data.Accounts.Single();
        var nav=new ManualPerformanceValuation(account.Id,day,10,20,3,4,5,true,"合成结单");
        // 模拟导入账户尚未确认币种，不能仅凭界面勾选完整性绕过。
        await cases.ImportManualAsync(System.Text.Json.JsonSerializer.Serialize(new PerformanceData
            {Accounts=[new("UNCONFIRMED","IBKR","未确认","USD",day,BaseCurrencyConfirmed:false)]}));
        await Reject(()=>cases.AddValuationAsync(nav with{AccountId="UNCONFIRMED"}));
        await Reject(()=>cases.AddValuationAsync(nav with{AccountId="MISSING"}));
        await Reject(()=>cases.AddValuationAsync(nav with{Complete=false}));
        await Reject(()=>cases.AddValuationAsync(nav with{Cash=-1}));
        await Reject(()=>cases.AddValuationAsync(nav with{Source=" "}));
        if((await cases.LoadAsync()).Data.Valuations.Length!=0)throw new Exception("无效手工净值产生写入。");
        await cases.ConfirmBaseCurrencyAsync(account.Id,"HKD");
        await cases.AddValuationAsync(nav);
        var valuation=(await cases.LoadAsync()).Data.Valuations.Single();
        if(valuation.AsOf.Offset!=CRS.Timing.BeijingClock.Offset || valuation.AsOf.Hour!=23 || valuation.AsOf.Date!=day.ToDateTime(TimeOnly.MinValue))
            throw new Exception("手工日终估值没有直接保存北京时间。");
        if(valuation.Components.Any(x=>x.Currency!="HKD") || valuation.Quality!=PerformanceQuality.Estimated || valuation.Components.Length!=5)
            throw new Exception("净值没有使用当前已确认币种或被提升为已核验。");
        var coverage=new ManualPerformanceCoverage(account.Id,day,day,false,false,"合成核对");
        await Reject(()=>cases.ConfirmCoverageAsync(coverage));
        await Reject(()=>cases.ConfirmCoverageAsync(coverage with{From=day.AddDays(1),FlowsComplete=true}));
        if((await cases.LoadAsync()).Data.Coverage.Length!=0)throw new Exception("缺失确认被当作零流量。");
        await cases.ConfirmCoverageAsync(coverage with{FlowsComplete=true});
        var confirmed=(await cases.LoadAsync()).Data.Coverage.Single();
        if(!confirmed.ExternalFlowsComplete || confirmed.IncomeComplete || confirmed.CostsComplete)throw new Exception("覆盖确认擅自扩大收入成本范围。");
        var entry=new ManualPerformanceEvent(account.Id,day,InvestmentEventKind.Deposit,20,"HKD","合成流水","","",0,0,0,true);
        await Reject(()=>cases.AddEventAsync(entry with{AccountId=null}));
        await cases.AddEventAsync(entry);
        var storedEvent=(await cases.LoadAsync()).Data.Events.Single();
        if(storedEvent.Amount!=20 || storedEvent.Time.Offset!=CRS.Timing.BeijingClock.Offset || storedEvent.Time.Hour!=12)
            throw new Exception("应用层事件金额或北京时间中午假定错误。");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try {await cases.AddEventAsync(entry,canceled.Token);throw new Exception("取消仍写入。");}catch(OperationCanceledException){}
        if((await cases.LoadAsync()).Data.Events.Length!=1)throw new Exception("取消新增事件。");
        Console.WriteLine("PASS 手工绩效应用用例：账户、当前币种、完整性、金额、来源、事件和取消（SQLCipher隔离库）。");
    }

    private static async Task Reject(Func<Task> action)
    {
        try {await action();}catch(CrsException){return;}
        throw new Exception("无效手工输入未拒绝。");
    }
}
