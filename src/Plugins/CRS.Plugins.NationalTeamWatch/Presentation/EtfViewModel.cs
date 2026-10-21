using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>仅管理界面状态和交互命令，数据判断与存储交由应用用例。</summary>
public partial class EtfViewModel(IEtfWorkspace workspace, CancellationToken lifetime) : ObservableObject
{
    public ExitAlertViewModel Alerts {get;}=new(workspace,lifetime);
    public bool CanManage=>workspace.CanManage;
    public ObservableCollection<EtfSummary> Rows { get; }=[];
    public ObservableCollection<EtfConfiguration> Configurations {get;}=[];
    private ICollectionView? configurationView;
    public ICollectionView ConfigurationView=>configurationView??=CreateConfigurationView();
    public string ConfigurationCount=>$"监控中 {Configurations.Count(x=>x.Active)} 只 · 目录共 {Configurations.Count} 只";
    public string ConfigurationTitle=>SelectedConfiguration is null?"新增ETF":"修改ETF";
    public bool IsNewConfiguration=>SelectedConfiguration is null;
    public bool CanEditConfiguration=>CanManage && !Busy;
    public IReadOnlyList<string> Markets {get;}=["SSE","SZSE"];
    [ObservableProperty] private EtfConfiguration? selectedConfiguration;
    [ObservableProperty] private string configurationSearch="";
    [ObservableProperty] private bool monitorEnabled;
    public ObservableCollection<EvidenceRow> Evidence { get; }=[];
    public ObservableCollection<CoverageDay> CoverageDays { get; }=[];
    public ObservableCollection<CollectionAttempt> CollectionAttempts { get; }=[];
    [ObservableProperty] private DateTime? coverageFrom=MarketToday.AddYears(-1);
    [ObservableProperty] private DateTime? coverageTo=MarketToday;
    [ObservableProperty] private string coverageStatus="选择ETF和日期区间，查询已登记交易日历口径的覆盖率。";
    [ObservableProperty] private EtfSummary? selected;
    [ObservableProperty] private EvidenceRow? selectedEvidence;
    [ObservableProperty] private string reviewer="";
    [ObservableProperty] private bool confirmedComparable;
    [ObservableProperty] private string status="正在加载研究数据；NAV金额不可用。";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private DateTime? syncFrom=MarketToday.AddDays(-35);
    [ObservableProperty] private DateTime? syncTo=MarketToday;
    [ObservableProperty] private ISeries[] series=[];
    [ObservableProperty] private bool allEtfs=true;
    [ObservableProperty] private string chartStatus="选择ETF即可自动加载图表，也可切换全部监控ETF汇总。";
    public Task HistoryLoadTask {get;private set;}=Task.CompletedTask;
    public Task EvidenceLoadTask {get;private set;}=Task.CompletedTask;
    private int evidenceVersion;
    private int chartVersion;
    private bool refreshingRows;
    [ObservableProperty] private LiveChartsCore.SkiaSharpView.Axis[] xAxes=[];
    [ObservableProperty] private string market="SSE";
    [ObservableProperty] private string ticker="";
    [ObservableProperty] private string securityName="";
    [ObservableProperty] private string indexGroup="";
    [ObservableProperty] private string reason="";
    private CancellationTokenSource? operation;
    private Task? initialRefresh;
    /// <summary>同一会话的首次页面加载共享刷新任务，导航重建视图不会重复查询。</summary>
    public Task EnsureInitializedAsync()=>initialRefresh??=RefreshCommand.ExecuteAsync(null);
    private int selectionVersion;
    // 研究日期以北京时间为准，不依赖电脑的本地时区。
    private static DateTime MarketToday=>CRS.Timing.BeijingClock.Now.Date;
    private ICollectionView CreateConfigurationView()
    {
        var view=CollectionViewSource.GetDefaultView(Configurations);
        view.Filter=item=>item is EtfConfiguration row && (string.IsNullOrWhiteSpace(ConfigurationSearch) || $"{row.Market} {row.Ticker} {row.Name} {row.IndexGroup}".Contains(ConfigurationSearch.Trim(),StringComparison.OrdinalIgnoreCase));
        return view;
    }
    partial void OnConfigurationSearchChanged(string value)=>ConfigurationView.Refresh();
    partial void OnBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEditConfiguration));
    partial void OnSelectedConfigurationChanged(EtfConfiguration? value)
    {
        if(value is not null){Market=value.Market;Ticker=value.Ticker;SecurityName=value.Name;IndexGroup=value.IndexGroup;Reason=value.Reason;MonitorEnabled=value.Active;}
        OnPropertyChanged(nameof(ConfigurationTitle));OnPropertyChanged(nameof(IsNewConfiguration));
    }
    partial void OnSelectedChanged(EtfSummary? value)
    {
        // 切换证券后立即清掉旧图表和覆盖率，避免把旧证券的数据展示到新选择上。
        InvalidateCoverage();
        if(refreshingRows)return;
        EvidenceLoadTask=LoadEvidenceAsync(lifetime);
        // 点击具体ETF时回到单只模式；快速切换由独立版本号保护，不受其它操作Busy阻挡。
        if(value is not null && AllEtfs)AllEtfs=false;
        else if(!refreshingRows)HistoryLoadTask=LoadHistoryAsync();
    }
    partial void OnAllEtfsChanged(bool value)
    {
        if(value && !refreshingRows)
        {
            refreshingRows=true;Selected=null;refreshingRows=false;
            EvidenceLoadTask=LoadEvidenceAsync(lifetime);
        }
        if(!refreshingRows)HistoryLoadTask=LoadHistoryAsync();
    }
    partial void OnCoverageFromChanged(DateTime? value)=>InvalidateCoverage();
    partial void OnCoverageToChanged(DateTime? value)=>InvalidateCoverage();
    private void InvalidateCoverage()
    {
        // 输入在后台查询期间也可能改变，用版本号拒绝旧请求回写到新选择。
        selectionVersion++;CoverageDays.Clear();CollectionAttempts.Clear();CoverageStatus="选择ETF和日期区间后查询；刷新数据后需要重新查询。";
    }
    private async Task ExecuteAsync(Func<CancellationToken,Task> work,CancellationToken cancellationToken=default)
    {
        if(Busy || lifetime.IsCancellationRequested) return;
        // 自动任务同时响应宿主调度取消和插件会话取消，手动命令继续支持界面取消。
        Busy=true; operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime,cancellationToken);
        try {await work(operation.Token);}
        catch(OperationCanceledException) {Status="已停止等待；服务已接受的任务可能继续运行，请在任务管理中查询或取消。";}
        catch(MissingMethodException error)
        {
            // 保留缺失的方法签名（不回显堆栈、路径、令牌或原始HTTP内容），
            // 帮助分辨插件/核心契约冲突与图表依赖问题，而非只有异常类型。
            var signature=error.Message.Replace("\r"," ").Replace("\n"," ");
            if(signature.Length>300)signature=signature[..300]+"…";
            Status="组件方法接口不匹配："+signature+
                "。请先关闭全部小财牛进程，再使用同一构建的完整安装包更新桌面程序及国家队插件；禁止仅覆盖某个DLL。";
        }
        catch(Exception error) {Status=error is InvalidDataException or CRS.Worker.Ipc.BackgroundServiceException?error.Message:"操作失败；类别："+error.GetType().Name;}
        finally {operation.Dispose();operation=null;Busy=false;}
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        await RefreshConfigurationAsync(token);
        Status=$"ETF清单已读取 {Configurations.Count} 项，正在读取份额概览…";
        var rows=await workspace.OverviewAsync(token);token.ThrowIfCancellationRequested();
        var selectedKey=Selected is {} current?new EtfKey(current.Market,current.Ticker):null;
        refreshingRows=true;
        Selected=null;SelectedEvidence=null;InvalidateCoverage();CoverageStatus="本地数据已刷新，请重新查询ETF覆盖率。";
        Rows.Clear();foreach(var row in rows) Rows.Add(row);
        Selected=rows.FirstOrDefault(x=>x.Market==selectedKey?.Market && x.Ticker==selectedKey.Ticker);
        refreshingRows=false;
        EvidenceLoadTask=LoadEvidenceAsync(token);await EvidenceLoadTask;
        HistoryLoadTask=LoadHistoryAsync();await HistoryLoadTask;
        await LoadMarketActivityAsync(token);
        Status=$"当前监控 {Rows.Count} 只 · {workspace.NetworkStatus}";
    }
    /// <summary>只读取当前证券的证据；选择变化时立即清空旧数据，并拒绝迟到的查询结果。</summary>
    private async Task LoadEvidenceAsync(CancellationToken token)
    {
        var version=++evidenceVersion;Evidence.Clear();SelectedEvidence=null;
        if(Selected is not {} row||token.IsCancellationRequested)return;
        try
        {
            var evidence=await workspace.EvidenceAsync(new EtfKey(row.Market,row.Ticker),token);
            if(version!=evidenceVersion||token.IsCancellationRequested)return;
            foreach(var item in evidence)Evidence.Add(item);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception error){if(version==evidenceVersion)Status="证据读取失败："+error.GetType().Name;}
    }
    /// <summary>编辑配置只刷新目录与概览，避免每次保存都重新下载全部历史证据。</summary>
    private async Task RefreshConfigurationAsync(CancellationToken token,EtfKey? select=null)
    {
        select??=SelectedConfiguration is { } current?new(current.Market,current.Ticker):null;
        var entries=await workspace.ConfigurationAsync(token);token.ThrowIfCancellationRequested();
        SelectedConfiguration=null;Configurations.Clear();foreach(var row in entries)Configurations.Add(row);
        SelectedConfiguration=Configurations.SingleOrDefault(x=>x.Market==select?.Market && x.Ticker==select.Ticker);
        OnPropertyChanged(nameof(ConfigurationCount));
    }
    [RelayCommand] private void NewConfiguration()
    {
        SelectedConfiguration=null;ConfigurationSearch="";Market="SSE";Ticker="";SecurityName="";IndexGroup="";Reason="";MonitorEnabled=false;
    }
    [RelayCommand] private Task RefreshConfiguration()=>ExecuteAsync(token=>RefreshConfigurationAsync(token));
    [RelayCommand] private Task SaveConfiguration()=>ExecuteAsync(async token=>
    {
        if(!CanManage)throw new InvalidDataException("保存配置需要管理员权限。");
        if(Market is not ("SSE" or "SZSE") || Ticker.Length!=6 || !Ticker.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(SecurityName) || string.IsNullOrWhiteSpace(IndexGroup) || string.IsNullOrWhiteSpace(Reason))throw new InvalidDataException("请填写市场、6位代码、名称、指数分组和配置理由。");
        if(IsNewConfiguration && Configurations.Any(x=>x.Market==Market && x.Ticker==Ticker))throw new InvalidDataException("该市场和代码已在目录中，请选择该行后修改。");
        var key=new EtfKey(Market,Ticker);var revision=SelectedConfiguration?.Revision?? -1;
        // 宿主保证插件写任务串行；长采集期间向用户说明保存可能排队。
        Status="正在保存ETF配置；如后台正在采集，将等待当前采集完成。可在任务中心查看执行记录。";
        await workspace.SaveConfigurationAsync(key,SecurityName,IndexGroup,MonitorEnabled,Reason,revision,token);
        ConfigurationSearch="";await RefreshConfigurationAsync(token,key);
        await RefreshAsync(token);
        Status=$"ETF配置已保存 · 监控中 {Rows.Count} 只；后台自动采集已启用的标的，停用保留已有历史。";
    });
    /// <summary>后台采集结束后的展示刷新不覆盖用户正在进行的交互。</summary>
    public Task RefreshFromScheduleAsync(CancellationToken token)=>Busy?Task.CompletedTask:RefreshAsync(token);
    [RelayCommand] private Task Refresh()=>ExecuteAsync(RefreshAsync);
    /// <summary>区间和命令属于界面交互；下载、解析与日历登记由后台用例执行。</summary>
    [RelayCommand] private Task SyncHistory()=>ExecuteAsync(async token=>
    {
        if(SyncFrom is null || SyncTo is null)throw new InvalidDataException("请设置采集日期区间。");
        await workspace.SyncAsync(DateOnly.FromDateTime(SyncFrom.Value),DateOnly.FromDateTime(SyncTo.Value),token);
        await RefreshAsync(token);
    });
    public Task SyncLatestAsync(CancellationToken cancellationToken)=>ExecuteAsync(async token=>
    {
        var today=DateOnly.FromDateTime(MarketToday);
        try{await workspace.SyncAsync(today.AddDays(-7),today,token);}
        finally{if(!token.IsCancellationRequested)await RefreshAsync(token);}
    },cancellationToken);
    [RelayCommand] private void Cancel()=>operation?.Cancel();
    [RelayCommand] private Task ShowHistory()=>HistoryLoadTask=LoadHistoryAsync();
    private async Task LoadHistoryAsync()
    {
        var version=++chartVersion;
        historyPoints=[];hasHistory=false;RebuildComparison();
        if(lifetime.IsCancellationRequested)return;
        if(!AllEtfs && Selected is null){ChartStatus="选择ETF即可自动加载图表。";return;}
        var title=AllEtfs?"全部监控ETF（固定当前篮子）":$"{Selected!.Ticker} {Selected.Name}";
        var key=AllEtfs?null:new EtfKey(Selected!.Market,Selected.Ticker);
        ChartStatus=title+" · 正在加载…";
        try
        {
            var points=await workspace.HistoryAsync(key,lifetime);
            if(version!=chartVersion || lifetime.IsCancellationRequested)return;
            historyPoints=points;hasHistory=true;RebuildComparison();
            var latest=points.LastOrDefault();
            ChartStatus=title+(latest is null?" · 暂无历史数据":$" · 截至 {latest.Date:yyyy-MM-dd} · 有效 {latest.Covered}/{latest.Total} 只 · 净变化 {(latest.DeltaShares is {} delta?delta.ToString("+#,##0;-#,##0;0"):"不可计算")}")+"。正值为份额净增加，负值为净减少；折线连接有效观测点，空缺日期不补值；缺份额或日历时净变化不可计算。份额相加不代表资金金额或国家队实际买卖。";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception error)
        {
            if(version==chartVersion)ChartStatus="图表加载失败："+(error is InvalidDataException?error.Message:"请确认后台服务已更新且可连接（"+error.GetType().Name+"）。");
        }
    }
    /// <summary>界面只展示后台覆盖判定；250日是一期目标，不能把尚未登记的日历当成已覆盖。</summary>
    [RelayCommand] private Task ShowCoverage()=>ExecuteAsync(async token=>
    {
        if(Selected is null || CoverageFrom is null || CoverageTo is null)throw new InvalidDataException("请先选择ETF和完整日期区间。");
        var requestedVersion=selectionVersion;
        var key=new EtfKey(Selected.Market,Selected.Ticker);var from=DateOnly.FromDateTime(CoverageFrom.Value);var to=DateOnly.FromDateTime(CoverageTo.Value);
        var report=await workspace.CoverageAsync(key,from,to,token);
        token.ThrowIfCancellationRequested();if(requestedVersion!=selectionVersion)return;
        var attempts=await workspace.CollectionAttemptsAsync(key,from,to,token);
        token.ThrowIfCancellationRequested();if(requestedVersion!=selectionVersion)return;
        CoverageDays.Clear();foreach(var day in report.Days)CoverageDays.Add(day);
        CollectionAttempts.Clear();foreach(var attempt in attempts)CollectionAttempts.Add(attempt);
        CoverageStatus=$"{report.Market}:{report.Ticker} · 已登记交易日 {report.RegisteredTradingDays} · 有效 {report.AcceptedDays} · 缺失 {report.MissingDays} · 待审核 {report.PendingDays} · 证据异常 {report.CriticalDays} · 拒绝 {report.RejectedDays} · 覆盖率 {(report.CoveragePercent is null?"不可判定":report.CoveragePercent.Value+"%")} · {report.StatusText}。一期目标250个有效交易日；本比例只针对所选区间内已登记的日历。";
    });
    [RelayCommand] private Task ExportOriginal()=>ExportEvidence(true);
    [RelayCommand] private Task ExportNormalized()=>ExportEvidence(false);
    /// <summary>选择导出目的地属于交互，读取原件、验证和写入仍交给应用用例。</summary>
    private Task ExportEvidence(bool original)=>ExecuteAsync(async token=>
    {
        if(SelectedEvidence is null)throw new InvalidDataException("请先选择一条证据修订。");
        var dialog=new Microsoft.Win32.SaveFileDialog {FileName=SelectedEvidence.Ticker+"-"+SelectedEvidence.TradeDate+(original?"-original.bin":"-normalized.csv"),Filter=original?"原始证据字节|*.bin|所有文件|*.*":"标准化CSV|*.csv",OverwritePrompt=true};
        if(dialog.ShowDialog()!=true)return;
        await workspace.ExportEvidenceAsync(SelectedEvidence.RevisionId,original,dialog.FileName,token);Status=$"B · 市场客观数据 · 截至 {SelectedEvidence.TradeDate}：已导出份额证据；ETF全市场份额不代表机构持仓或买卖，原始文件内容保持不变。";
    });
    [RelayCommand] private Task Approve()=>Review(true);
    [RelayCommand] private Task Reject()=>Review(false);
    private Task Review(bool accept)=>ExecuteAsync(async token=>
    { if(SelectedEvidence is null) return; await workspace.ReviewAsync(SelectedEvidence.RevisionId,accept,Reviewer,ConfirmedComparable,token);await RefreshAsync(token); });
    [RelayCommand] private Task AddToPool()=>Membership(true);
    [RelayCommand] private Task RemoveFromPool()=>Membership(false);
    private Task Membership(bool active)=>ExecuteAsync(async token=>
    {await workspace.MembershipAsync(new(Market,Ticker),SecurityName,IndexGroup,active,Reason,token);await RefreshAsync(token);});
}
