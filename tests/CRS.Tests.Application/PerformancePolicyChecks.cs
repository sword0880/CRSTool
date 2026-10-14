using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain;
using CRS.Domain.Performance;

internal static class PerformancePolicyChecks
{
    internal static async Task RunAsync()
    {
        var identity=new PerformanceAccountIdentity("FUTU:A","合成开户姓名","12345678");
        var account=new InvestmentAccount("FUTU:A","FUTU","富途 合成开户姓名（12345678）","HKD",new(2025,1,1));
        var privacy=new PerformanceAccountPrivacy([account],[identity]);
        if(privacy.Account(account)!="富途账户 · 尾号 5678" || privacy.Text(account.Alias+" / 12345678").Contains("12345678") || privacy.Text(account.Alias).Contains("合成开户姓名"))throw new Exception("默认账户展示泄露完整身份。");
        var custom=account with{Alias="家庭账户"};
        if(new PerformanceAccountPrivacy([custom],[identity]).Account(custom)!="家庭账户 · 尾号 5678" || !privacy.Details(account).Contains("12345678"))throw new Exception("别名/末四位或主动查看不正确。");
        if(!privacy.Text("旧别名 87654321").Contains("****4321"))throw new Exception("旧别名未遮蔽长账号。");
        using var session=new CancellationTokenSource();var store=new Store();var market=new Market();
        var cases=new PerformanceUseCases(store,new Files(),session.Token,market);
        var request=new PerformanceRequest(["A"],new(2025,1,1),new(2025,1,2),"USD");
        await Rejected(()=>cases.FetchBenchmarkAsync("SPX",request));
        if(cases.OnlineUpdatesAllowed || market.Calls!=0 || store.Imports!=0)throw new Exception("默认离线仍触发网络。");
        await cases.CalculateAsync(request);
        if(market.Calls!=0)throw new Exception("离线计算触发网络。");
        if(!cases.SetOnlineUpdatesAllowed(true) || !await cases.FetchBenchmarkAsync("SPX",request) || market.Calls!=1 || store.Imports!=1)throw new Exception("明确授权后不能更新指数。");
        market.Wait=true;market.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);var fetching=cases.FetchBenchmarkAsync("HSI",request);await market.Started.Task;
        cases.SetOnlineUpdatesAllowed(false);await Rejected(()=>fetching);
        if(store.Imports!=1 || cases.OnlineUpdatesAllowed)throw new Exception("切回离线后仍提交下载结果。");
        await Rejected(()=>cases.FetchBenchmarkAsync("SPX",request));
        if(market.Calls!=2)throw new Exception("撤销授权后仍请求网络。");
        market.Wait=false;cases.SetOnlineUpdatesAllowed(true);session.Cancel();
        if(cases.OnlineUpdatesAllowed)throw new Exception("锁定后仍保留在线授权状态。");
        await Rejected(()=>cases.FetchBenchmarkAsync("SPX",request));
        if(cases.SetOnlineUpdatesAllowed(true) || market.Calls!=2)throw new Exception("锁后旧会话能够重新授权联网。");
        Console.WriteLine("绩效隐私/联网策略通过：别名与末四位、旧别名遮蔽、主动查看、默认离线、显式授权、撤销中断及锁后拒绝。");
    }
    private static async Task Rejected(Func<Task> action)
    {try{await action();}catch(CrsException){return;}catch(OperationCanceledException){return;}throw new Exception("应拒绝的绩效联网操作被允许。");}
    private sealed class Market:IPerformanceMarketData
    {
        internal int Calls;internal bool Wait;internal TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PerformanceDocument> FetchAsync(string symbol,DateOnly from,DateOnly to,string currency,CancellationToken cancellation)
        {Calls++;Started.TrySetResult();if(Wait)await Task.Delay(Timeout.Infinite,cancellation);return new("synthetic","index",new(),[],"MarketJSON");}
    }
    private sealed class Store:IPerformanceStore
    {
        internal int Imports;
        public PerformanceWorkspace Load()=>new(new(){Accounts=[new("A","SYNTHETIC","合成账户","USD",new(2025,1,1))]},[],[]);
        public bool Import(PerformanceDocument document){Imports++;return true;}
        public void SaveRun(PerformanceRun run){}
        public PerformanceRun OpenRun(string id)=>throw new NotSupportedException();
        public void SavePortfolio(InvestmentPortfolio portfolio){}
        public void RenameAccount(string id,string alias){}
        public void ConfirmBaseCurrency(string id,string currency){}
        public void SourceRevision(PerformanceSourceRevision revision){}
        public void Revise(ClassificationRevision revision){}
        public void Transfer(TransferLink link){}
    }
    private sealed class Files:IPerformanceFiles
    {
        public string Hash(PerformanceData data)=>PerformanceSnapshot.Identity(data);
        public PerformanceDocument Read(string path,string broker,CancellationToken cancellation)=>throw new NotSupportedException();
        public PerformanceDocument ReadManual(string json)=>throw new NotSupportedException();
        public void Export(PerformanceRun run,string path,bool encrypted,string password,SensitiveExportConsent? consent,CancellationToken cancellation)=>throw new NotSupportedException();
    }
}
