using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;

internal static class AnnualAggregationChecks
{
    /// <summary>真实仓储验证跨券商汇总、来源保留、拒绝门槛及取消不保存。</summary>
    public static async Task RunAsync(string root)
    {
        var store = TestStores.Create(Path.Combine(root,"annual-aggregation"));
        // 不提供配置文件，证明年度汇总只使用父任务冻结汇率。
        var workflow = new DesktopWorkflow(new DesktopOperations(store,Path.Combine(root,"missing-config")));
        var a = Source("A","IBKR","SAME",100); var b = Source("B","FUTU","SAME",-100);
        store.Save(a); store.Save(b);
        var originalA = System.Text.Json.JsonSerializer.Serialize(store.Load(a.SnapshotId));
        var aggregate = await workflow.AggregateAnnualAsync(["A","B"],true,CancellationToken.None);
        var loaded = store.Load(aggregate.SnapshotId);
        if (!loaded.IsAnnualAggregate || loaded.Broker != "MULTI" || loaded.AggregationSources.Count != 2
            || loaded.Summary.GainCny != 0 || loaded.Summary.GainTax != 0 || loaded.Summary.DividendCny != 200
            || loaded.Summary.SupplementTax != 40 || loaded.ImportedTradeCount != 4 || loaded.EstimatedTopUpCny != 40
            || !store.QueryHistory(1,50).Rows.Any(r=>r.Id==loaded.SnapshotId && r.IsAnnualAggregate)
            || System.Text.Json.JsonSerializer.Serialize(store.Load(a.SnapshotId)) != originalA)
            throw new Exception("跨券商年度汇总金额、来源关系、持久化或原任务保护失败。");
        var path = Path.Combine(root,"annual.xlsx"); await workflow.ExportAsync(path,loaded,"");
        var sourceRows = ExcelReports.ReadSheet(path,"年度汇总来源");
        if (sourceRows.Count != 3 || !sourceRows.Skip(1).Select(r=>r[1]).Order().SequenceEqual(new[]{"FUTU","IBKR"}))
            throw new Exception("年度汇总底稿未披露券商及来源任务。");

        // 每次拒绝都检查总数，不能留下部分汇总记录。
        async Task Reject(string[] ids,bool confirmed=true)
        {
            var count = store.QueryHistory(1,50).TotalCount;
            try { await workflow.AggregateAnnualAsync(ids,confirmed,CancellationToken.None); throw new Exception("无效汇总被接受。"); }
            catch (CrsException) { }
            if (store.QueryHistory(1,50).TotalCount != count) throw new Exception("汇总拒绝后仍保存任务。");
        }
        await Reject(["A","B"],false); await Reject(["A","A"]); await Reject(["A"]);
        await Reject(["A",loaded.SnapshotId]);
        store.Save(Source("DUPLICATE","IBKR","SAME",10)); await Reject(["A","DUPLICATE"]);
        store.Save(Source("YEAR","FUTU","OTHER",10,2024)); await Reject(["A","YEAR"]);
        store.Save(Source("RATE","FUTU","OTHER",10,rate:2)); await Reject(["A","RATE"]);
        store.Save(Source("SCOPE","FUTU","OTHER",10,scope:"ANOTHER")); await Reject(["A","SCOPE"]);
        var legacy = new CalculationResult { Year=2025,Broker="FUTU",Summary=new(0,0,0,0,0,0,0),
            CoveredAccounts=["OLD"],SnapshotId="OLD" };
        store.Save(legacy);
        var partial = await workflow.AggregateAnnualAsync(["A","OLD"],true,CancellationToken.None);
        if (partial.EstimatedTopUpCny is not null || partial.DataCompleteness != DataCompleteness.Incomplete
            || !partial.Issues.Any(i=>i.Code=="TAX_FACTS_MISSING"))
            throw new Exception("缺少年度事实的来源被升级为完整税额。");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var before = store.QueryHistory(1,50).TotalCount;
        try { await workflow.AggregateAnnualAsync(["A","B"],true,cts.Token); throw new Exception("取消汇总仍执行。"); }
        catch (OperationCanceledException) { }
        if (store.QueryHistory(1,50).TotalCount != before) throw new Exception("取消汇总留下半成品。");
        Console.WriteLine("年度汇总验证通过：跨券商及盈亏合并、来源持久化与导出、归属及重复门槛、年度／汇率／纳税人隔离、旧事实保持未知、取消不保存（6 项）。");
    }
    /// <summary>已完成的来源任务保留原币事实，两个券商可使用相同账户编号。</summary>
    private static CalculationResult Source(string id,string broker,string account,decimal gain,int year=2025,decimal rate=1,string scope="LOCAL_USER")
    {
        var key = new SecurityKey(account,"STOCK","USD",broker);
        var income = new Income(account,year,"USD",100,0);
        var match = new Match(key,"STOCK",new(year,1,1),new(year,2,1),1,200-gain,200,0,0,gain,gain,"BUY","SELL",id+".xml",id+".xml");
        var summary = TaxEngine.Calculate([income],[match],new Dictionary<string,decimal>(),year,(_,_)=>rate);
        return new() { Year=year,Broker=broker,SnapshotId=id,Summary=summary,TaxpayerScopeId=scope,ImportedTradeCount=2,
            CoveredAccounts=[account],Matches=[match],Rates=[new(year,"USD",rate,"synthetic","","",null,null,false,true)],
            TaxInputs=new(1,TaxEngine.PolicyVersion,[income],[],broker),CalculationStatus=CalculationStatus.Completed,
            DataCompleteness=DataCompleteness.Confirmed,ReconciliationStatus=ReconciliationStatus.Matched,
            UsageLabel=UsageLabel.ReviewReady,Reconciled=true,EstimatedTopUpCny=summary.SupplementTax };
    }
}
