using CRS.Application;
using CRS.Application.Performance;
using CRS.Domain;
using CRS.Domain.Performance;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;

internal static class PerformanceInteractionChecks
{
    /// <summary>检查真实页面模型的边界、历史参数、取消和锁后清理，不访问用户真实资料。</summary>
    internal static async Task RunAsync()
    {
        var cases=new Cases();var interaction=new Interaction();var shell=new ShellViewModel(new FakeUseCases(),interaction,performance:cases,documentPassword:()=>"Synthetic-Encrypted-Password");var vm=shell.Performance;
        await vm.RefreshCommand.ExecuteAsync(null);vm.Currency="USD";vm.From=new(2024,12,31);vm.To=new(2025,12,31);
        if(!vm.OnlineUpdatesAllowed || vm.Benchmark!="SSE" || !vm.Benchmarks.Contains("SPX") || vm.Accounts.Single().Alias.Contains("12345678") || !vm.Accounts.Single().Alias.Contains("5678") || vm.HasAccountDetails)throw new Exception("账户默认展示或联网基准不正确。");
        vm.RevealAccountCommand.Execute(vm.Accounts.Single());if(!vm.AccountDetails.Contains("12345678"))throw new Exception("无法主动查看完整账号。");
        vm.HideAccountDetailsCommand.Execute(null);if(vm.HasAccountDetails)throw new Exception("完整账号未收起。");
        await vm.CalculateCommand.ExecuteAsync(null);
        if(cases.MarketRequests!=1 || !cases.Forced || cases.LastBenchmark!="SSE")throw new Exception("重算未在线更新上证指数。");
        await vm.RefreshCurveCommand.ExecuteAsync(null);
        if(cases.MarketRequests!=2)throw new Exception("更新曲线绕过了联网请求。");
        if(vm.History.Any(h=>h.Scope.Contains("12345678")) || vm.Summary.Contains("12345678") || vm.AccountResults.Any(a=>a.AccountAlias.Contains("12345678")))throw new Exception("历史或汇总泄露完整账号。");
        if(!vm.Metrics.Any(m=>m.Value=="不可计算") || !vm.ExportCommand.CanExecute(null) || cases.Request?.AccountIds.Single()!="A")throw new Exception("绩效缺项或边界展示错误。");
        if(!vm.HasNetAssetsPoints || !vm.HasComparisonPoints)throw new Exception("有效曲线点未显示。");
        cases.FailMarket=true;await vm.CalculateCommand.ExecuteAsync(null);
        if(vm.HasComparisonPoints || vm.ExportCommand.CanExecute(null) || vm.Metrics.Count!=0 || !vm.ComparisonStatus.Contains("联网更新失败"))throw new Exception("联网失败仍沿用旧结果。");
        cases.FailMarket=false;await vm.CalculateCommand.ExecuteAsync(null);
        var summary=vm.Summary;await vm.RefreshCommand.ExecuteAsync(null);
        if(!vm.ExportCommand.CanExecute(null) || !vm.HasNetAssetsPoints || vm.Summary!=summary)throw new Exception("重新进入页面或刷新同一资料清空了刚计算的曲线。");
        vm.Currency="CNY";
        if(vm.HasNetAssetsPoints || !vm.NetAssetsStatus.Contains("更新曲线") || !vm.RefreshCurveCommand.CanExecute(null))throw new Exception("参数改变后只剩空坐标轴或无法直接重算。");
        await vm.RefreshCurveCommand.ExecuteAsync(null);
        if(cases.Request?.Currency!="CNY" || !vm.HasNetAssetsPoints)throw new Exception("更新曲线未重新计算失效结果。");
        cases.Data=cases.Data with{Coverage=[new("A",new(2024,12,31),new(2025,12,31),true,new("SYNTHETIC","coverage"))]};
        await vm.RefreshCommand.ExecuteAsync(null);await vm.CalculateCommand.ExecuteAsync(null);
        cases.Data=cases.Data with{Coverage=[cases.Data.Coverage.Single() with{ExternalFlowsComplete=false}]};
        await vm.RefreshCommand.ExecuteAsync(null);
        if(vm.ExportCommand.CanExecute(null) || vm.HasNetAssetsPoints)throw new Exception("同条数的覆盖证据变化未使当前结果失效。");
        await vm.CalculateCommand.ExecuteAsync(null);
        cases.CalculationGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCalculation=vm.CalculateCommand.ExecuteAsync(null);vm.Currency="HKD";
        cases.CalculationGate.SetResult();await oldCalculation;cases.CalculationGate=null;
        if(vm.ExportCommand.CanExecute(null) || vm.Metrics.Count!=0 || vm.HasNetAssetsPoints)throw new Exception("旧任务结束覆盖了新参数。");
        await vm.CalculateCommand.ExecuteAsync(null);
        cases.CurveGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCurve=vm.RefreshCurveCommand.ExecuteAsync(null);vm.Benchmark="HSI";
        cases.CurveGate.SetResult();await oldCurve;cases.CurveGate=null;
        if(vm.HasComparisonPoints)throw new Exception("旧指数任务覆盖了新指数选择。");
        cases.CalculationGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        oldCalculation=vm.CalculateCommand.ExecuteAsync(null);
        cases.Data=cases.Data with{Coverage=[cases.Data.Coverage.Single() with{ExternalFlowsComplete=true}]};
        cases.CalculationGate.SetResult();await oldCalculation;cases.CalculationGate=null;
        if(vm.ExportCommand.CanExecute(null))throw new Exception("旧输入任务在数据更新后仍作为当前结果展示。");
        await vm.CalculateCommand.ExecuteAsync(null);
        cases.LoadGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh=vm.RefreshCommand.ExecuteAsync(null);vm.Accounts.Single().Selected=false;
        cases.LoadGate.SetResult();await refresh;cases.LoadGate=null;
        if(vm.Accounts.Single().Selected || vm.ExportCommand.CanExecute(null))throw new Exception("刷新任务恢复了用户已取消的旧账户选择。");
        vm.Accounts.Single().Selected=true;await vm.CalculateCommand.ExecuteAsync(null);
        shell.Import.Year=2024;if(!vm.ExportCommand.CanExecute(null))throw new Exception("税务年度清掉独立绩效。");
        vm.Accounts.Single().Selected=false;if(vm.ExportCommand.CanExecute(null) || vm.CalculateCommand.CanExecute(null) || vm.Metrics.Count!=0)throw new Exception("范围改变沿用旧结果。");
        await vm.RefreshCommand.ExecuteAsync(null);if(vm.Accounts.Single().Selected)throw new Exception("刷新自动勾回已取消账户。");
        vm.Accounts.Single().Selected=true;await vm.CalculateCommand.ExecuteAsync(null);shell.State.IsBusy=true;
        if(vm.CalculateCommand.CanExecute(null) || vm.RefreshCommand.CanExecute(null) || vm.ExportCommand.CanExecute(null))throw new Exception("跨页忙碌门槛失效。");shell.State.IsBusy=false;
        vm.SelectedHistory=vm.History.Single();await vm.OpenHistoryCommand.ExecuteAsync(null);vm.Currency="HKD";await vm.OpenHistoryCommand.ExecuteAsync(null);
        if(!vm.Summary.Contains("USD"))throw new Exception("历史误用当前报告币种。");
        interaction.Consent=false;await vm.ExportCommand.ExecuteAsync(null);if(cases.Exports!=0)throw new Exception("未确认明文就导出。");
        interaction.Consent=true;await vm.ExportCommand.ExecuteAsync(null);if(cases.Exports!=1 || cases.Consent?.PlaintextConfirmed!=true || cases.Consent.TemporaryLocationConfirmed)throw new Exception("明文授权或 TEMP 策略失效。");
        await vm.ExportEncryptedCommand.ExecuteAsync(null);if(cases.Password!="Synthetic-Encrypted-Password")throw new Exception("加密导出密码传递/清理失败。");
        cases.Wait=true;var calculation=vm.CalculateCommand.ExecuteAsync(null);if(!shell.State.IsBusy)throw new Exception("未进入忙碌状态。");vm.CancelCommand.Execute(null);await calculation;
        if(shell.State.IsBusy || !shell.State.Status.Contains("取消"))throw new Exception("绩效取消没有恢复状态。");
        vm.RevealAccountCommand.Execute(vm.Accounts.Single());vm.ManualJson="synthetic-private";vm.ClearForLock();
        if(vm.HasAccountDetails || vm.OnlineUpdatesAllowed || cases.OnlineUpdatesAllowed)throw new Exception("锁定未撤销授权/清理主动查看信息。");
        if(vm.Accounts.Count!=0 || vm.History.Count!=0 || vm.Metrics.Count!=0 || vm.Events.Count!=0 || vm.Exposure.Count!=0 || vm.ManualJson!="" || vm.RefreshCommand.CanExecute(null))throw new Exception("锁定未清理敏感状态或仍可刷新。");
        Console.WriteLine("绩效界面通过：账户选择、缺项展示、税务隔离、参数失效、历史冻结、跨页忙碌、导出确认/密码清理、取消与锁定清理。");
    }
    private sealed class Interaction:IUserInteraction
    {
        public bool Consent;
        public bool ConfirmPlaintextExport()=>Consent;
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string name)=>"synthetic-output";
        public void ShowError(string message){if(!message.Contains("synthetic network failure"))throw new Exception(message);}
        public void OpenFolder(string path){}
    }
    private sealed class Cases:IPerformanceUseCases
    {
        public PerformanceData Data=new(){Accounts=[new("A","IBKR","合成账户","USD",new(2024,12,31))]};
        private readonly PerformanceRun run=new("0123456789abcdef",null,DateTimeOffset.UtcNow,"synthetic",new(),new("Performance.v1",new(["A"],new(2024,12,31),new(2025,12,31),"USD"),[new("Twr","TWR",null,"比例",PerformanceQuality.InsufficientData,"缺净值")],[],[],[new("合成账户","IBKR",null,null,null,null,null,null,"USD")],[],new(2024,12,31)));
        public PerformanceRequest? Request;public bool Wait;public int Exports;public SensitiveExportConsent? Consent;public string Password="";
        public bool OnlineUpdatesAllowed {get;private set;}
        public int MarketRequests;public bool FailMarket;public bool Forced;public string LastBenchmark="";
        public bool SetOnlineUpdatesAllowed(bool authorized)=>OnlineUpdatesAllowed=authorized;
        public Task<bool> FetchBenchmarkAsync(string symbol,PerformanceRequest request,CancellationToken cancellation=default,bool force=false){if(FailMarket)throw new CrsException("synthetic network failure");MarketRequests++;Forced=force;LastBenchmark=symbol;return Task.FromResult(false);}
        public TaskCompletionSource? CalculationGate,CurveGate,LoadGate;
        public async Task<PerformanceCurve> CurveAsync(string id,string benchmark,CancellationToken cancellation=default)
        {if(CurveGate is not null)await CurveGate.Task;return new PerformanceCurve([new(new(2024,12,31),100,null,100),new(new(2025,12,31),110,null,112)],benchmark,"合成曲线");}
        public async Task<PerformanceWorkspace> LoadAsync(CancellationToken cancellation=default)
        {if(LoadGate is not null)await LoadGate.Task;return new PerformanceWorkspace(Data,[],[new(run.Id,run.CreatedAt,run.Result.Request.From,run.Result.Request.To,"USD","合成账户 12345678","synthetic")]){AccountIdentities=[new("A","合成开户姓名","12345678")]};}
        public Task<bool> ImportAsync(string path,string broker,CancellationToken cancellation=default)=>Task.FromResult(true);
        public Task<bool> ImportManualAsync(string json,CancellationToken cancellation=default)=>Task.FromResult(true);
        public Task SavePortfolioAsync(InvestmentPortfolio portfolio,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task RenameAccountAsync(string id,string alias,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ConfirmBaseCurrencyAsync(string id,string currency,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task SetSourceIncludedAsync(string hash,bool included,string reason,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ReviseClassificationAsync(string eventId,InvestmentEventKind kind,bool confirmed,string reason,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ConfirmTransferAsync(string outgoingId,string incomingId,decimal tolerance,string reason,string toleranceCurrency="CNY",CancellationToken cancellation=default)=>Task.CompletedTask;
        public async Task<PerformanceRun> CalculateAsync(PerformanceRequest request,CancellationToken cancellation=default)
        {Request=request;var input=Data;if(Wait)await Task.Delay(Timeout.Infinite,cancellation);if(CalculationGate is not null)await CalculationGate.Task;return run with{Input=input,InputHash=PerformanceSnapshot.Identity(input),Result=run.Result with{Request=request}};}
        public Task<PerformanceRun> OpenRunAsync(string id,CancellationToken cancellation=default)=>Task.FromResult(run);
        public Task<PerformanceRun> ReplayAsync(string id,CancellationToken cancellation=default)=>Task.FromResult(run);
        public Task ExportAsync(string id,string path,bool encrypted,string password,SensitiveExportConsent? consent=null,CancellationToken cancellation=default){Exports++;Consent=consent;Password=password;return Task.CompletedTask;}
    }
}
