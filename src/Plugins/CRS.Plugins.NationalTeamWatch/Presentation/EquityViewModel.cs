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
public partial class EquityViewModel(IEtfWorkspace workspace,CancellationToken lifetime):ObservableObject
{
    public bool CanManage=>workspace.CanManage;
    public ObservableCollection<EquityHoldingRow> Rows {get;}=[];
    public ObservableCollection<EquityWatch> Watches {get;}=[];
    public ObservableCollection<EquityDisclosure> Disclosures {get;}=[];
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
        ReviewInstitution=value is not null&&ReviewInstitutions.Contains(value.Institution)?value.Institution:null;
        DetailTask=LoadDetailAsync(value);
    }
    partial void OnSelectedWatchChanged(EquityWatch? value)
    {if(value is not null){WatchMarket=value.Market;WatchTicker=value.Ticker;WatchName=value.Name;WatchActive=value.Active;WatchFavorite=value.Favorite;}}
    partial void OnSelectedDisclosureChanged(EquityDisclosure? value)
    {
        ReportAsOf=value?.ReportAsOf?.ToDateTime(TimeOnly.MinValue);Published=value?.Published?.ToDateTime(TimeOnly.MinValue);
        DocumentUrl=value?.DocumentUrl??"";Page=value?.Page??"";OriginalConfirmed=false;Comparable=false;
    }
    private async Task LoadRowsAsync()
    {
        var version=++queryVersion;var old=Selected;Status="正在查询披露持仓…";
        try
        {
            var rows=await workspace.EquityHoldingsAsync(Search,Institution=="全部机构"?null:Institution,Nature switch{"证券投资持仓"=>"Portfolio","战略控股/参股"=>"Strategic","性质待核验"=>"Unknown",_=>null},Favorites,lifetime);
            if(version!=queryVersion||lifetime.IsCancellationRequested)return;
            Rows.Clear();foreach(var row in rows)Rows.Add(row);
            Selected=rows.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker&&x.Account==old.Account&&x.State==old.State)
                ??rows.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker&&x.Account==old.Account);
            if(Selected is null){Disclosures.Clear();HoldingSeries=[];HoldingAxes=[];DetailStatus="选择持仓查看披露时间线。";}
            Status=$"当前筛选 {rows.Count} 条 · 已核验 {rows.Count(x=>x.ReportAsOf.HasValue&&x.State is not ("Critical" or "NeedsReview" or "Conflict"))} 条 · 待核验 {rows.Count(x=>x.State is "NeedsReview" or "Conflict")} 条。观察列表仅代表采集范围，不代表机构已确认持有。";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(version==queryVersion){Rows.Clear();Selected=null;Disclosures.Clear();HoldingSeries=[];HoldingAxes=[];Status="直接持股查询失败："+(error is System.IO.InvalidDataException?error.Message:error.GetType().Name);}}
    }
    private async Task LoadDetailAsync(EquityHoldingRow? row)
    {
        var version=++detailVersion;Disclosures.Clear();SelectedDisclosure=null;HoldingSeries=[];HoldingAxes=[];
        if(row is null){DetailStatus="选择持仓查看披露时间线。";return;}
        DetailStatus=$"{row.Ticker} {row.Name} · {row.Account} · 正在加载披露证据…";
        try
        {
            var items=await workspace.EquityDisclosuresAsync(new(row.Market,row.Ticker),lifetime);
            if(version!=detailVersion||lifetime.IsCancellationRequested)return;
            foreach(var item in items)Disclosures.Add(item);SelectedDisclosure=items.FirstOrDefault(x=>x.Id==row.DisclosureId);
            // 只把已确认的离散快照映射到图表，不补值、不连线、不计算业务增减持。
            var points=items.Where(x=>x.State=="Accepted"&&x.ReportAsOf.HasValue).OrderBy(x=>x.ReportAsOf).ToArray();
            var values=points.Select((x,i)=>(Index:i,Holder:x.Holders.FirstOrDefault(h=>h.Account==row.Account))).Where(x=>x.Holder is not null).Select(x=>new ObservablePoint(x.Index,(double)x.Holder!.Shares)).ToArray();
            HoldingSeries=values.Length>0?[new ScatterSeries<ObservablePoint>{Name="已披露持股（股）",Values=values,GeometrySize=10}]:[];
            HoldingAxes=[new(){Labels=points.Select(x=>x.ReportAsOf!.Value.ToString("yyyy-MM-dd")).ToArray(),MinStep=1}];
            DetailStatus=$"{row.Ticker} {row.Name} · {row.Account} · {row.StatusText}。持仓仅展示报告时点；来源日期不是持仓截止日。股价及个股成交额尚未接入，不能据此判断每日交易。";
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
        await workspace.ReviewEquityAsync(new(SelectedDisclosure.Id,DateOnly.FromDateTime(ReportAsOf.Value),DateOnly.FromDateTime(Published.Value),DocumentUrl,Page,Reviewer,Comparable,Strategic?"Strategic":"Portfolio",Account:Selected.Account,OriginalConfirmed:OriginalConfirmed,Institution:ReviewInstitution),lifetime);
        await RefreshAsync();
    });
}
