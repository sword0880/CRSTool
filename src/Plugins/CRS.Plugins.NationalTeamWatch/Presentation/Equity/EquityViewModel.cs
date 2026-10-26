using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>直接持股交互与服务端结果展示；不在桌面下载公告、写库或推断持股变化。</summary>
public partial class EquityViewModel(IDirectEquityWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public bool CanManage=>workspace.CanManage;
    public ObservableCollection<EquityHoldingRow> Rows {get;}=[];
    public ObservableCollection<EquityWatch> Watches {get;}=[];
    public ObservableCollection<EquityDisclosure> Disclosures {get;}=[];
    public ObservableCollection<EquityHoldingRow> Events {get;}=[];
    public IReadOnlyList<string> Institutions {get;}=["全部机构","汇金投资","汇金资管","证金公司","社保基金","养老保险基金","主体待核验"];
    public IReadOnlyList<string> ReviewInstitutions {get;}=["汇金投资","汇金资管","证金公司","社保基金","养老保险基金"];
    [ObservableProperty] private string? reviewInstitution;
    public IReadOnlyList<string> Natures {get;}=["全部性质","证券投资持仓","战略控股/参股","性质待核验"];
    public IReadOnlyList<string> Markets {get;}=["SSE","SZSE"];
    [ObservableProperty] private string search="";
    [ObservableProperty] private string institution="全部机构";
    [ObservableProperty] private string nature="全部性质";
    [ObservableProperty] private bool favorites;
    [ObservableProperty] private string status="等待后台披露数据。";
    [ObservableProperty] private string detailStatus="选择持仓查看披露时间线。";
    [ObservableProperty] private bool busy;
    [ObservableProperty] private EquityHoldingRow? selected;
    [ObservableProperty] private EquityWatch? selectedWatch;
    [ObservableProperty] private EquityDisclosure? selectedDisclosure;
    [ObservableProperty] private ISeries[] holdingSeries=[];
    [ObservableProperty] private Axis[] holdingAxes=[];
    [ObservableProperty] private string watchMarket="SSE";
    [ObservableProperty] private string watchTicker="";
    [ObservableProperty] private string watchName="";
    [ObservableProperty] private bool watchActive=true;
    [ObservableProperty] private bool watchFavorite;
    [ObservableProperty] private DateTime? reportAsOf;
    [ObservableProperty] private DateTime? published;
    [ObservableProperty] private string documentUrl="";
    [ObservableProperty] private string page="";
    [ObservableProperty] private string reviewer="";
    [ObservableProperty] private bool comparable;
    [ObservableProperty] private bool originalConfirmed;
    [ObservableProperty] private bool strategic;
    // 未主动确认性质时保持Unknown，不能用未勾选战略来默认推定证券投资。
    [ObservableProperty] private bool natureConfirmed;
    partial void OnNatureConfirmedChanged(bool value){if(!value)Comparable=false;}
    private EquityDisclosure? ComparisonDisclosure=>ReportAsOf is {} report?Disclosures
        .Where(x=>x.State is "Accepted" or "Critical"&&x.ReportAsOf is {} date&&date<DateOnly.FromDateTime(report))
        .OrderByDescending(x=>x.ReportAsOf).FirstOrDefault():null;
    public string ComparisonStatus=>ComparisonDisclosure is {} prior?$"比较前期：{prior.ReportAsOf:yyyy-MM-dd}，公告日 {prior.Published:yyyy-MM-dd}。请核对与本期的可比性。":"尚无已核验前期；仅登记本期快照，不计算期间差额。";
    partial void OnReportAsOfChanged(DateTime? value){Comparable=false;OnPropertyChanged(nameof(ComparisonStatus));}
    private int queryVersion,detailVersion;
    public Task LoadTask {get;private set;}=Task.CompletedTask;
    public Task DetailTask {get;private set;}=Task.CompletedTask;
    partial void OnSearchChanged(string value)=>LoadTask=LoadRowsAsync();
    partial void OnInstitutionChanged(string value)=>LoadTask=LoadRowsAsync();
    partial void OnNatureChanged(string value)=>LoadTask=LoadRowsAsync();
    partial void OnFavoritesChanged(bool value)=>LoadTask=LoadRowsAsync();
    partial void OnSelectedChanged(EquityHoldingRow? value)
    {
        ReportAsOf=null;Published=null;DocumentUrl="";Page="";OriginalConfirmed=false;Comparable=false;
        Strategic=value?.Nature=="Strategic";
        NatureConfirmed=false;
        ReviewInstitution=value is not null&&ReviewInstitutions.Contains(value.Institution)?value.Institution:null;
        SetStockAccounts(value);
        DetailTask=LoadDetailAsync(value);
    }
    partial void OnSelectedWatchChanged(EquityWatch? value)
    {if(value is not null){WatchMarket=value.Market;WatchTicker=value.Ticker;WatchName=value.Name;WatchActive=value.Active;WatchFavorite=value.Favorite;}}
    partial void OnSelectedDisclosureChanged(EquityDisclosure? value)
    {
        ReportAsOf=value?.ReportAsOf?.ToDateTime(TimeOnly.MinValue);Published=value?.Published?.ToDateTime(TimeOnly.MinValue);
        DocumentUrl=value?.DocumentUrl??"";Page=value?.Page??"";OriginalConfirmed=false;Comparable=false;NatureConfirmed=false;
        OnPropertyChanged(nameof(ComparisonStatus));
    }
    private async Task LoadRowsAsync()
    {
        var version=++queryVersion;var old=Selected;Status="正在查询披露持仓…";
        try
        {
            var rows=await workspace.EquityHoldingsAsync(Search,Institution=="全部机构"?null:Institution,Nature switch{"证券投资持仓"=>"Portfolio","战略控股/参股"=>"Strategic","性质待核验"=>"Unknown",_=>null},Favorites,lifetime);
            if(version!=queryVersion||lifetime.IsCancellationRequested)return;
            Rows.Clear();foreach(var row in rows)Rows.Add(row);
            RebuildEquityOverview();
            Selected=rows.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker&&x.Account==old.Account&&x.State==old.State)
                ??rows.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker&&x.Account==old.Account);
            if(Selected is null){Disclosures.Clear();Events.Clear();ClearEquityCharts();SetStockAccounts(null);DetailStatus="选择持仓查看披露时间线。";}
            Status=$"当前筛选 {rows.Count} 条 · 已核验 {rows.Count(x=>x.ReportAsOf.HasValue&&x.State is not ("Critical" or "NeedsReview" or "Conflict"))} 条 · 待核验 {rows.Count(x=>x.State is "NeedsReview" or "Conflict")} 条。观察列表仅代表采集范围，不代表机构已确认持有。";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(version==queryVersion){Rows.Clear();RebuildEquityOverview();Selected=null;Disclosures.Clear();Events.Clear();ClearEquityCharts();SetStockAccounts(null);Status="直接持股查询失败："+(error is System.IO.InvalidDataException?error.Message:error.GetType().Name);}}
    }
    private async Task LoadDetailAsync(EquityHoldingRow? row)
    {
        var version=++detailVersion;Disclosures.Clear();Events.Clear();SelectedDisclosure=null;ClearEquityCharts();
        Reports.Clear();SelectedReport=null;
        ReportStatus=row is null?"选择股票后查找官方报告。":"正在读取已缓存的官方报告目录…";
        if(row is null){DetailStatus="选择持仓查看披露时间线。";return;}
        DetailStatus=$"{row.Ticker} {row.Name} · {row.Account} · 正在加载披露证据…";
        try
        {
            var items=await workspace.EquityDisclosuresAsync(new(row.Market,row.Ticker),lifetime);
            var events=await workspace.EquityEventsAsync(new(row.Market,row.Ticker),lifetime);
            if(version!=detailVersion||lifetime.IsCancellationRequested)return;
            foreach(var item in items)Disclosures.Add(item);SelectedDisclosure=items.FirstOrDefault(x=>x.Id==row.DisclosureId);
            foreach(var item in events.Where(x=>x.Account==row.Account&&x.Institution==row.Institution))Events.Add(item);
            RebuildEquityCharts();
            DetailStatus=$"{row.Ticker} {row.Name} · {row.Account} · {row.StatusText}。持仓仅展示报告时点；来源日期不是持仓截止日。股价及个股成交额尚未接入，不能据此判断每日交易。";
            await LoadReportDocumentsAsync(row,version);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(version==detailVersion)DetailStatus="披露明细加载失败："+error.GetType().Name;}
    }
    public async Task RefreshAsync()
    {
        var watches=await workspace.EquityWatchlistAsync(lifetime);var old=SelectedWatch;
        Watches.Clear();foreach(var watch in watches)Watches.Add(watch);SelectedWatch=watches.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker);
        LoadTask=LoadRowsAsync();await LoadTask;
    }
    private async Task ExecuteAsync(Func<Task> work)
    {
        if(Busy||lifetime.IsCancellationRequested)return;Busy=true;
        try{await work();}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){Status=error is System.IO.InvalidDataException?error.Message:"操作失败："+error.GetType().Name;}
        finally{Busy=false;}
    }
    [RelayCommand] private Task Refresh()=>ExecuteAsync(RefreshAsync);
    [RelayCommand] private Task Sync()=>ExecuteAsync(async()=>{try{await workspace.SyncEquityAsync(lifetime);}finally{if(!lifetime.IsCancellationRequested)await RefreshAsync();}});
    [RelayCommand] private Task SaveWatch()=>ExecuteAsync(async()=>{await workspace.SaveEquityWatchAsync(new(WatchMarket,WatchTicker,WatchName,WatchActive,WatchFavorite),lifetime);await RefreshAsync();});
    [RelayCommand] private void NewWatch(){SelectedWatch=null;WatchMarket="SSE";WatchTicker="";WatchName="";WatchActive=true;WatchFavorite=false;}
    [RelayCommand] private Task ToggleFavorite()=>ExecuteAsync(async()=>
    {
        if(Selected is null)return;var watch=Watches.Single(x=>x.Market==Selected.Market&&x.Ticker==Selected.Ticker);
        await workspace.SaveEquityWatchAsync(watch with{Favorite=!watch.Favorite},lifetime);await RefreshAsync();
    });
    [RelayCommand] private Task Review()=>ExecuteAsync(async()=>
    {
        if(Selected is null||SelectedDisclosure is null||SelectedDisclosure.Market!=Selected.Market||SelectedDisclosure.Ticker!=Selected.Ticker||ReportAsOf is null||Published is null||!OriginalConfirmed)throw new System.IO.InvalidDataException("请选择披露记录，并填写报告截止日、公告日及官方PDF；须逐项核对完整股东表、单位、股类及持股数。");
        await workspace.ReviewEquityAsync(new(SelectedDisclosure.Id,DateOnly.FromDateTime(ReportAsOf.Value),DateOnly.FromDateTime(Published.Value),DocumentUrl,Page,Reviewer,Comparable,!NatureConfirmed?"Unknown":Strategic?"Strategic":"Portfolio",Account:Selected.Account,OriginalConfirmed:OriginalConfirmed,Institution:ReviewInstitution){ComparisonDisclosureId=ComparisonDisclosure?.Id},lifetime);
        await RefreshAsync();
    });
}
