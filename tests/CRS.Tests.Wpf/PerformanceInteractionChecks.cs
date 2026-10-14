using CRS.Application;
using CRS.Application.Performance;
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
        await vm.CalculateCommand.ExecuteAsync(null);
        if(!vm.Metrics.Any(m=>m.Value=="不可计算") || !vm.ExportCommand.CanExecute(null) || cases.Request?.AccountIds.Single()!="A")throw new Exception("绩效缺项或边界展示错误。");
        if(!vm.HasNetAssetsPoints || !vm.HasComparisonPoints)throw new Exception("有效曲线点未显示。");
        var summary=vm.Summary;await vm.RefreshCommand.ExecuteAsync(null);
        if(!vm.ExportCommand.CanExecute(null) || !vm.HasNetAssetsPoints || vm.Summary!=summary)throw new Exception("重新进入页面或刷新同一资料清空了刚计算的曲线。");
        vm.Currency="CNY";
        if(vm.HasNetAssetsPoints || !vm.NetAssetsStatus.Contains("更新曲线") || !vm.RefreshCurveCommand.CanExecute(null))throw new Exception("参数改变后只剩空坐标轴或无法直接重算。");
        await vm.RefreshCurveCommand.ExecuteAsync(null);
        if(cases.Request?.Currency!="CNY" || !vm.HasNetAssetsPoints)throw new Exception("更新曲线未重新计算失效结果。");
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
        vm.ManualJson="synthetic-private";vm.ClearForLock();
        if(vm.Accounts.Count!=0 || vm.History.Count!=0 || vm.Metrics.Count!=0 || vm.Events.Count!=0 || vm.Exposure.Count!=0 || vm.ManualJson!="" || vm.RefreshCommand.CanExecute(null))throw new Exception("锁定未清理敏感状态或仍可刷新。");
        Console.WriteLine("绩效界面通过：账户选择、缺项展示、税务隔离、参数失效、历史冻结、跨页忙碌、导出确认/密码清理、取消与锁定清理。");
    }
    private sealed class Interaction:IUserInteraction
    {
        public bool Consent;
        public bool ConfirmPlaintextExport()=>Consent;
        public string[] PickFiles(string filter,bool multiple)=>[];
        public string? SaveFile(string filter,string name)=>"synthetic-output";
        public void ShowError(string message)=>throw new Exception(message);
        public void OpenFolder(string path){}
    }
    private sealed class Cases:IPerformanceUseCases
    {
        private readonly PerformanceData data=new(){Accounts=[new("A","IBKR","合成账户","USD",new(2024,12,31))]};
        private readonly PerformanceRun run=new("0123456789abcdef",null,DateTimeOffset.UtcNow,"synthetic",new(),new("Performance.v1",new(["A"],new(2024,12,31),new(2025,12,31),"USD"),[new("Twr","TWR",null,"比例",PerformanceQuality.InsufficientData,"缺净值")],[],[],[new("合成账户","IBKR",null,null,null,null,null,null,"USD")],[],new(2024,12,31)));
        public PerformanceRequest? Request;public bool Wait;public int Exports;public SensitiveExportConsent? Consent;public string Password="";
        public Task<PerformanceCurve> CurveAsync(string id,string benchmark,CancellationToken cancellation=default)=>Task.FromResult(new PerformanceCurve([new(new(2024,12,31),100,null,100),new(new(2025,12,31),110,null,112)],benchmark,"合成曲线"));
        public Task<PerformanceWorkspace> LoadAsync(CancellationToken cancellation=default)=>Task.FromResult(new PerformanceWorkspace(data,[],[new(run.Id,run.CreatedAt,run.Result.Request.From,run.Result.Request.To,"USD","合成账户","synthetic")]));
        public Task<bool> ImportAsync(string path,string broker,CancellationToken cancellation=default)=>Task.FromResult(true);
        public Task<bool> ImportManualAsync(string json,CancellationToken cancellation=default)=>Task.FromResult(true);
        public Task SavePortfolioAsync(InvestmentPortfolio portfolio,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task RenameAccountAsync(string id,string alias,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ConfirmBaseCurrencyAsync(string id,string currency,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task SetSourceIncludedAsync(string hash,bool included,string reason,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ReviseClassificationAsync(string eventId,InvestmentEventKind kind,bool confirmed,string reason,CancellationToken cancellation=default)=>Task.CompletedTask;
        public Task ConfirmTransferAsync(string outgoingId,string incomingId,decimal tolerance,string reason,string toleranceCurrency="CNY",CancellationToken cancellation=default)=>Task.CompletedTask;
        public async Task<PerformanceRun> CalculateAsync(PerformanceRequest request,CancellationToken cancellation=default){Request=request;if(Wait)await Task.Delay(Timeout.Infinite,cancellation);return run;}
        public Task<PerformanceRun> OpenRunAsync(string id,CancellationToken cancellation=default)=>Task.FromResult(run);
        public Task<PerformanceRun> ReplayAsync(string id,CancellationToken cancellation=default)=>Task.FromResult(run);
        public Task ExportAsync(string id,string path,bool encrypted,string password,SensitiveExportConsent? consent=null,CancellationToken cancellation=default){Exports++;Consent=consent;Password=password;return Task.CompletedTask;}
    }
}
