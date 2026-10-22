using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.DesktopClient.Services;
using CRS.DesktopClient.ViewModels;
// 独立窗口验收只查询公开后台任务，不创建或解锁个人保险库。
if(args is ["--beijing-time"]){BeijingTimeChecks.Run();return;}
if(args is ["--history-date"]){await HistoryDateChecks.RunAsync();return;}
if(args is ["--cron"]){CronEditorChecks.Run();return;}
if(args is ["--settings-ui",var settingsDirectory]){SettingsViewChecks.Run(settingsDirectory);return;}
if(args is ["--shell-brand-preview",var brandDirectory]){ShellBrandPreview.Run(brandDirectory);return;}
if(args is ["--compact-navigation",var navigationDirectory]){CompactNavigationChecks.Run(navigationDirectory);return;}
if(args is ["--shell-status-checks"]){ShellStatusBarChecks.Run();return;}
if(args is ["--task-center-deeplink-checks"]){TaskCenterDeepLinkChecks.Run();return;}
if(args is ["--task-center-refresh-checks"]){await TaskCenterRefreshChecks.RunAsync();return;}
if(args is ["--task-activity-checks"]){await TaskActivityChecks.RunAsync();return;}
if(args is ["--task-button-checks"]){await TaskCenterButtonChecks.RunAsync();return;}
if(args is ["--worker-status-preview",var workerDirectory]){WorkerStatusPreview.Run(workerDirectory);return;}
if(args is ["--performance-component-preview",var componentDirectory]){PerformanceComponentPreview.Run(componentDirectory);return;}
if(args is ["--plugin-management-preview",var pluginManagementDirectory]){PluginManagementPreview.Run(pluginManagementDirectory);return;}
if(args is ["--execution-log-ui",var logScreenshot]){ExecutionLogViewChecks.Run(logScreenshot);return;}
if(args.Contains("--background-ui")){var index=Array.IndexOf(args,"--background-ui");BackgroundServiceViewChecks.Run(args[index+1],args.ElementAtOrDefault(index+2));return;}
var cases=new FakeUseCases();
var shell=new ShellViewModel(cases,new SilentInteraction());
await shell.State.DisplayAsync(cases.Result);
if (!shell.Reports.ExportCommand.CanExecute(null) || shell.Reports.ExportCarryCommand.CanExecute(null) || shell.Dashboard.IssueCount!="1") throw new Exception("结果未同步至概览 / 导出。");
var frozenStatus=shell.State.ResultStatus;shell.State.Status="汇率读取完成";
if (shell.State.ResultStatus!=frozenStatus) throw new Exception("操作提示覆盖了冻结核算状态。");
shell.State.IsBusy=true;
if (shell.Import.CalculateCommand.CanExecute(null) || shell.Review.SaveCommand.CanExecute(null) || shell.Reports.ExportCommand.CanExecute(null)) throw new Exception("跨页面仍可并发操作。");
shell.State.IsBusy=false;
shell.Review.Evidence="上一份资料";shell.Review.Confirmed=true;shell.Import.Year=2024;
if (shell.State.HasResult || shell.Review.Confirmed || shell.Review.Evidence!="" || shell.Results.IncomeChart.Series.Length!=0 || shell.Reports.ExportCommand.CanExecute(null)) throw new Exception("跨年度沿用了旧结果。");
await shell.History.RefreshCommand.ExecuteAsync(null);
if (shell.Dashboard.RecentTasks.Count!=1) throw new Exception("概览任务列表与历史未联动。");
shell.History.Selected=shell.History.Rows.Single();await shell.History.OpenCommand.ExecuteAsync(null);
if (shell.State.Fingerprint!="" || shell.State.DisplayYear!=2025) throw new Exception("历史恢复没有使用冻结上下文。");
await shell.State.DisplayAsync(new() {Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="ZERO",ImportedTradeCount=0});
if (shell.Dashboard.TradeCount!="0") throw new Exception("真实零笔被显示为未知。");
await shell.State.DisplayAsync(new() {Year=2025,Summary=new(0,0,0,0,0,0,0),SnapshotId="LEGACY"});
if (shell.Dashboard.TradeCount!="未登记") throw new Exception("旧记录缺失笔数被当作零。");
Console.WriteLine("WPF 模型验证通过：结果联动、并发门槛、输入失效、任务列表和历史（5 项）。");
await ImportInteractionChecks.RunAsync();
await HistoryInteractionChecks.RunAsync();
await AnnualAggregationInteractionChecks.RunAsync();
await ForeignCreditInteractionChecks.RunAsync();
await VaultInteractionChecks.RunAsync();
AutomaticLockChecks.Run();
await ReportsInteractionChecks.RunAsync();
await EncryptedReportsInteractionChecks.RunAsync();
await CanonicalViewChecks.RunAsync();
await TaxAmountViewChecks.RunAsync();
await PerformanceInteractionChecks.RunAsync();

sealed class SilentInteraction : IUserInteraction
{
    public string[] PickFiles(string filter,bool multiple)=>[];
    public string? SaveFile(string filter,string fileName)=>null;
    public void ShowError(string message)=>throw new Exception(message);
    public void OpenFolder(string path)=>throw new NotSupportedException();
}
class FakeUseCases : IDesktopUseCases
{
    public virtual Task<CalculationResult> ReviewForeignCreditAsync(string id,string path,CancellationToken cancellation)=>Task.FromResult(Result);
    public DesktopCalculationRequest? LastRequest { get; private set; }
    public CalculationResult Result {get;}=new() {Year=2025,Summary=new(1,2,3,1,1,0,2),SnapshotId="SYNTHETIC",Issues=[new("SYNTHETIC","合成问题")],ImportedTradeCount=2};
    public string ConfigPath=>"unused";public string ReadQueryId()=>"";public void LogFailure(string exceptionType){}
    public Task<DesktopCalculationOutcome> CalculateAsync(DesktopCalculationRequest request,CancellationToken cancellation)
    { LastRequest = request; return Task.FromResult(new DesktopCalculationOutcome(Result,"synthetic")); }
    public Task<List<HistoryItem>> HistoryAsync()=>Task.FromResult(new List<HistoryItem>{new("SYNTHETIC",2025,"synthetic",false,1,"IBKR")});
    public virtual async Task<HistoryPageResult> QueryHistoryAsync(int pageNumber,int pageSize) => new(await HistoryAsync(),1,1,pageSize);
    public Task<OriginalFileRequirements> OriginalFileRequirementsAsync(string id)=>Task.FromResult(new OriginalFileRequirements(["synthetic.xml"],null));
    public Task VerifyOriginalFilesAsync(string id,string[] files,string? opening,CancellationToken cancellation)=>Task.CompletedTask;
    public virtual Task<CalculationResult> ReplayAsync(string id,CancellationToken cancellation)=>Task.FromResult(Result);
    public virtual Task<CalculationResult> AggregateAnnualAsync(string[] sourceIds,bool ownershipConfirmed,CancellationToken cancellation)=>Task.FromResult(Result);
    public Task<CalculationResult> LoadAsync(string id)=>Task.FromResult(Result);
    public Task<List<ReviewNote>> ReviewsAsync(string id)=>Task.FromResult(new List<ReviewNote>());
    public Task SaveReviewAsync(string id,string category,string evidence)=>Task.CompletedTask;
    public virtual Task ExportAsync(string path,CalculationResult result,string fingerprint,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)=>Task.CompletedTask;
    public virtual Task ExportCarryAsync(string path,CalculationResult result,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)=>Task.CompletedTask;
    // 加密交互测试可覆盖独立用例，不在替身中执行密码学或文件操作。
    public virtual Task ExportEncryptedAsync(string path,CalculationResult result,string fingerprint,string password,CancellationToken cancellation=default)=>Task.CompletedTask;
    public virtual Task<string> RestoreEncryptedAsync(string path,string parentDirectory,string password,CancellationToken cancellation=default,SensitiveExportConsent? consent=null)=>Task.FromResult("synthetic-restored");
    public Task DownloadAsync(string queryId,string token,DateOnly from,DateOnly to,string path,CancellationToken cancellation)=>Task.CompletedTask;
    public Task<List<AppliedRate>> RatesAsync(int year)=>Task.FromResult(new List<AppliedRate>());
    public virtual IReadOnlyList<Trade> Trades(CalculationResult result)=>[];
    public IReadOnlyList<ReportSection> Sections(CalculationResult result,IReadOnlyList<ReviewNote> reviews)=>[new("税务汇总",TaxSummaryPresentation.Rows(result)),new("待复核",result.Issues)];
}
