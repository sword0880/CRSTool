using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>股票入口与持有人选择联动；证据分组由领域投影提供。</summary>
public partial class EquityViewModel
{
    public IReadOnlyList<string> StockListScopes {get;}=["已核验持股","其他披露 / 待核验"];
    public ObservableCollection<EquityStockSummary> Stocks {get;}=[];
    public ObservableCollection<EquityHoldingRow> StockHolders {get;}=[];
    [ObservableProperty] private string stockListScope="已核验持股";
    [ObservableProperty] private EquityStockSummary? selectedStock;
    [ObservableProperty] private int detailTabIndex;
    [ObservableProperty] private bool advancedTools;
    [ObservableProperty] private bool showEquityDetails;
    public string StockListStatus=>stockQueryFailed?"股票清单查询失败，请查看上方状态并重试。":Stocks.Count==0
        ?StockListScope==StockListScopes[0]?"暂无已核验持股；待核验资料可在“高级工具”查看。":"当前筛选暂无其他披露或待核验记录。"
        :$"当前 {Stocks.Count} 只股票，点击查看持有人。";
    public string HolderStatus=>SelectedStock is not {} stock?"选择股票查看持有人。"
        :$"{stock.Ticker} {stock.Name} · {stock.AccountCount} 个账户 · 报告期 {stock.ReportPeriodText}"
            +(StockListScope==StockListScopes[0]?"":" · 其他披露 / 待核验，不代表已确认持有");
    private bool rebuildingStocks,stockQueryFailed;
    partial void OnAdvancedToolsChanged(bool value)
    {
        if(value)return;
        MainTabIndex=0;Nature="全部性质";Favorites=false;StockListScope=StockListScopes[0];
    }
    partial void OnShowEquityDetailsChanged(bool value)
    {
        if(!value)DetailTabIndex=0;
        DetailTask=LoadDetailAsync(value?Selected:null);
    }
    partial void OnStockListScopeChanged(string value){if(!rebuildingStocks)RebuildStockList();}
    partial void OnSelectedStockChanged(EquityStockSummary? value)
    {
        if(rebuildingStocks)return;
        FillStockHolders();DetailTabIndex=0;
        Selected=StockHolders.FirstOrDefault();
    }
    private void FillStockHolders()
    {
        StockHolders.Clear();
        if(SelectedStock is {} stock)foreach(var holder in stock.Holders)StockHolders.Add(holder);
        OnPropertyChanged(nameof(HolderStatus));
    }
    private void RebuildStockList(EquityHoldingRow? preferred=null,bool selectAccount=true)
    {
        var market=preferred?.Market??SelectedStock?.Market;var ticker=preferred?.Ticker??SelectedStock?.Ticker;
        rebuildingStocks=true;
        try
        {
            Stocks.Clear();foreach(var stock in EquityStockList.Build(Rows,StockListScope==StockListScopes[0]))Stocks.Add(stock);
            SelectedStock=Stocks.FirstOrDefault(x=>x.Market==market&&x.Ticker==ticker);
            FillStockHolders();
        }
        finally{rebuildingStocks=false;}
        OnPropertyChanged(nameof(StockListStatus));
        if(selectAccount)
        {
            var next=StockHolders.FirstOrDefault(x=>x.Market==preferred?.Market&&x.Ticker==preferred.Ticker&&x.Account==preferred.Account&&x.Institution==preferred.Institution&&x.State==preferred.State)
                ??StockHolders.FirstOrDefault(x=>x.Account==preferred?.Account&&x.Institution==preferred.Institution)??StockHolders.FirstOrDefault();
            var unchanged=Selected==next;Selected=next;
            // 列表绑定可能在重建期间清空选择；即使新旧都为空，也须作废旧详情请求。
            if(unchanged)OnSelectedChanged(next);
        }
    }
    private void SyncStockSelection(EquityHoldingRow row)
    {
        if(StockHolders.Contains(row))return;
        rebuildingStocks=true;
        try{StockListScope=StockListScopes[EquityStockList.IsVerifiedHolding(row)?0:1];}
        finally{rebuildingStocks=false;}
        RebuildStockList(row,false);
        rebuildingStocks=true;
        try{Selected=row;}
        finally{rebuildingStocks=false;}
    }
}
