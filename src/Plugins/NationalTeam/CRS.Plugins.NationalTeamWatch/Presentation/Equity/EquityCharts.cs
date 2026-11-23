using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

public sealed record EquityAccountChoice(EquityHoldingRow Row)
{
    public string Label=>$"{Row.Institution} · {Row.Account}";
}
public sealed record EquityMatrixCell(string Text,string Detail);
public sealed record EquityMatrixStock(string Market,string Ticker,string Name,IReadOnlyDictionary<string,EquityMatrixCell> Cells)
{
    public EquityMatrixCell Huijin=>Cells["汇金投资"];
    public EquityMatrixCell Asset=>Cells["汇金资管"];
    public EquityMatrixCell Csf=>Cells["证金公司"];
    public EquityMatrixCell Social=>Cells["社保基金"];
    public EquityMatrixCell Pension=>Cells["养老保险基金"];
    public EquityMatrixCell Safe=>Cells["外汇局投资平台"];
    public EquityMatrixCell ChipFund=>Cells["国家大基金"];
    public EquityMatrixCell Pending=>Cells["主体待核验"];
}

/// <summary>只映射后台事实、分组标签及坐标；不在前台推算股数变化或机构归属。</summary>
public partial class EquityViewModel
{
    public ObservableCollection<EquityAccountChoice> StockAccounts {get;}=[];
    public ObservableCollection<EquityMatrixStock> Matrix {get;}=[];
    [ObservableProperty] private EquityAccountChoice? selectedAccount;
    [ObservableProperty] private EquityMatrixStock? selectedMatrix;
    [ObservableProperty] private int mainTabIndex;
    [ObservableProperty] private bool holdingPercent;
    [ObservableProperty] private ISeries[] deltaSeries=[];
    [ObservableProperty] private Axis[] deltaAxes=[];
    [ObservableProperty] private Axis[] holdingYAxes=[];
    [ObservableProperty] private Axis[] deltaYAxes=[];
    [ObservableProperty] private EquityHoldingRow? selectedEvent;
    [ObservableProperty] private string chartStatus="选择持仓查看报告期图表。";
    public string EventReadout=>SelectedEvent is {} row
        ?$"{row.EvidenceText} · 截止 {row.ReportAsOf:yyyy-MM-dd} · 公告 {row.Published:yyyy-MM-dd} · 比较起点 {(row.ComparisonFrom is {} from?from.ToString("yyyy-MM-dd"):"无前期")} · {row.StatusText} · {row.NatureText} · {row.Basis}"
        :"选择图中数据或报告记录查看时点与原文。";
    private EquityHoldingRow[] chartRows=[];
    private bool syncingEquityAxes;
    partial void OnSelectedAccountChanged(EquityAccountChoice? value){if(value is not null&&value.Row!=Selected)Selected=value.Row;}
    partial void OnHoldingPercentChanged(bool value)=>RebuildEquityCharts();
    partial void OnSelectedEventChanged(EquityHoldingRow? value)
    {
        OnPropertyChanged(nameof(EventReadout));
        if(value is not null)SelectedDisclosure=Disclosures.FirstOrDefault(x=>x.Id==value.DisclosureId);
    }
    private void RebuildEquityOverview()
    {
        var old=SelectedMatrix;Matrix.Clear();
        foreach(var stock in Rows.GroupBy(x=>(x.Market,x.Ticker,x.Name)))
        {
            var cells=new Dictionary<string,EquityMatrixCell>();
            foreach(var group in Institutions.Skip(1))
            {
                var accounts=stock.Where(x=>x.Institution==group).ToArray();
                var text=accounts.Length==0?"无记录":string.Join(" / ",accounts.GroupBy(x=>x.StatusText).Select(g=>$"{g.Key} {g.Count()}"));
                var detail=accounts.Length==0?"当前筛选无记录，不能据此认定未持有。":string.Join("\n",accounts.Select(x=>$"{x.Account} · {(x.ReportAsOf is {} date?date.ToString("yyyy-MM-dd"):"报告期待核验")} · {x.StatusText} · {x.NatureText}"));
                cells[group]=new(text,detail);
            }
            Matrix.Add(new(stock.Key.Market,stock.Key.Ticker,stock.Key.Name,cells));
        }
        SelectedMatrix=Matrix.FirstOrDefault(x=>x.Market==old?.Market&&x.Ticker==old.Ticker);
    }
    [RelayCommand] private void ShowMatrixStock()
    {
        if(SelectedMatrix is null)return;
        Selected=Rows.FirstOrDefault(x=>x.Market==SelectedMatrix.Market&&x.Ticker==SelectedMatrix.Ticker);MainTabIndex=0;
    }
    private void SetStockAccounts(EquityHoldingRow? row)
    {
        StockAccounts.Clear();
        if(row is not null)
            foreach(var item in Rows.Where(x=>x.Market==row.Market&&x.Ticker==row.Ticker).OrderByDescending(x=>x==row).DistinctBy(x=>(x.Institution,x.Account)))StockAccounts.Add(new(item));
        SelectedAccount=StockAccounts.FirstOrDefault(x=>x.Row==row);
    }
    private void ClearEquityCharts()
    {
        chartRows=[];HoldingSeries=[];HoldingAxes=[];HoldingYAxes=[];DeltaSeries=[];DeltaAxes=[];DeltaYAxes=[];SelectedEvent=null;
        ChartStatus="尚无已核验持仓点或可比增减；未列示不等于零持仓。";
    }
    private void RebuildEquityCharts()
    {
        chartRows=Events.Where(x=>x.ReportAsOf.HasValue).OrderBy(x=>x.ReportAsOf).ToArray();
        var holdings=new List<ObservablePoint>();var changes=new List<ObservablePoint>();
        for(var i=0;i<chartRows.Length;i++)
        {
            var row=chartRows[i];
            if(row.State is "NeedsReview" or "Critical" or "Conflict")continue;
            var value=HoldingPercent?row.Percent:row.Shares;
            if(value is {} shares)holdings.Add(new(i,(double)shares));
            if(row.State is "Increased" or "Decreased" or "Unchanged" && row.Delta is {} delta)changes.Add(new(i,(double)delta));
        }
        HoldingSeries=holdings.Count==0?[]:[new ScatterSeries<ObservablePoint>{Name=HoldingPercent?"已披露持股比例（%）":"已披露持股数（股）",Values=holdings,GeometrySize=11,YToolTipLabelFormatter=p=>ChartTooltip(p.Context.DataSource as ObservablePoint,false)}];
        DeltaSeries=changes.Count==0?[]:[new ColumnSeries<ObservablePoint>{Name="可比净增减（股）",Values=changes,MaxBarWidth=32,YToolTipLabelFormatter=p=>ChartTooltip(p.Context.DataSource as ObservablePoint,true)}];
        HoldingYAxes=[new(){Name=HoldingPercent?"占总股本（%）":"持股数（股）",TextSize=12,NameTextSize=13,MinLimit=0,MinStep=HoldingPercent?0:1,Labeler=value=>value.ToString(HoldingPercent?"0.####":"N0")}];
        var min=changes.Select(x=>x.Y!.Value).Append(0).Min();var max=changes.Select(x=>x.Y!.Value).Append(0).Max();var padding=Math.Max(1,(max-min)*.15);
        DeltaYAxes=[new(){Name="净增减（股）",TextSize=12,NameTextSize=13,MinStep=1,MinLimit=min-padding,MaxLimit=max+padding,Labeler=value=>value.ToString("+0;-0;0")}];
        Axis MakeAxis()=>new(){Labels=chartRows.Select(x=>x.ReportAsOf!.Value.ToString("yyyy-MM-dd")).ToArray(),Name="报告截止日（非公告日）",TextSize=12,NameTextSize=13,MinStep=1,MinLimit=-.6,MaxLimit=Math.Max(.6,chartRows.Length-.4)};
        var holdingAxis=MakeAxis();var deltaAxis=MakeAxis();HoldingAxes=[holdingAxis];DeltaAxes=[deltaAxis];
        holdingAxis.PropertyChanged+=(_,e)=>SyncEquityAxes(holdingAxis,deltaAxis,e.PropertyName);
        deltaAxis.PropertyChanged+=(_,e)=>SyncEquityAxes(deltaAxis,holdingAxis,e.PropertyName);
        ChartStatus=$"{holdings.Count} 个已核验持仓点 · {changes.Count} 个可比变化；首次列示、未列示、待核验及不可比不画增减柱。";
        SelectedEvent=Events.FirstOrDefault(x=>x==SelectedEvent)
            ??Events.FirstOrDefault(x=>x.DisclosureId==SelectedDisclosure?.Id)
            ??(SelectedDisclosure is null?Events.FirstOrDefault():null);
    }
    [RelayCommand] private void ResetEquityZoom()
    {
        if(HoldingAxes.Length==0)return;HoldingAxes[0].MinLimit=-.6;HoldingAxes[0].MaxLimit=Math.Max(.6,chartRows.Length-.4);
    }
    private void SyncEquityAxes(Axis source,Axis target,string? property)
    {
        if(syncingEquityAxes||property is not (nameof(Axis.MinLimit) or nameof(Axis.MaxLimit)))return;
        syncingEquityAxes=true;try{target.MinLimit=source.MinLimit;target.MaxLimit=source.MaxLimit;}finally{syncingEquityAxes=false;}
    }
    private string ChartTooltip(ObservablePoint? point,bool delta)
    {
        if(point?.X is not {} x||x<0||x>=chartRows.Length)return "无有效记录";
        var row=chartRows[(int)x];var value=delta?row.Delta:HoldingPercent?row.Percent:row.Shares;
        return $"{row.Account} · {row.EvidenceText}\n截止 {row.ReportAsOf:yyyy-MM-dd} · 公告 {row.Published:yyyy-MM-dd}\n{(delta?"报告期差额":"持仓")} {value:N4}{(!delta&&HoldingPercent?"%":"股")} · {row.StatusText}\n{row.Basis}";
    }
    [RelayCommand] private void SelectEquityPoint(ChartPoint? point)
    {
        if(point?.Context.DataSource is ObservablePoint {X:{} x}&&x>=0&&x<chartRows.Length)SelectedEvent=chartRows[(int)x];
    }
}
