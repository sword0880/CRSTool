using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Application;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EtfViewModel
{
    private IReadOnlyList<EtfHistoryPoint> historyPoints=[];
    private IReadOnlyList<MarketActivityPoint> activityPoints=[];
    private bool hasHistory;
    private DateOnly[] comparisonDates=[];
    private Dictionary<DateOnly,EtfHistoryPoint> historyByDate=[];
    private Dictionary<DateOnly,MarketActivityPoint> activityByDate=[];
    private bool syncingAxes;
    private int hoverIndex=-1;
    [ObservableProperty] private string comparisonReadout="悬停查看同日ETF份额、净增减与市场成交额。";
    [ObservableProperty] private RectangularSection[] shareSections=[];
    [ObservableProperty] private RectangularSection[] amountSections=[];
    public Margin ComparisonMargin {get;}=new(100,12,28,32);
    public Axis[] ShareYAxes {get;}=[new(){Labeler=value=>(value/100000000d).ToString("0.########")}];
    public Axis[] AmountYAxes {get;}=[new(){MinLimit=0,Labeler=value=>value.ToString("N0")}];

    // 这里只按日期对齐已由后台判定的结果；空值保持为空，不在展示层计算净变化或汇总。
    private void RebuildComparison()
    {
        foreach(var axis in XAxes.Concat(MarketAmountAxes))axis.PropertyChanged-=OnComparisonAxisChanged;
        historyByDate=historyPoints.ToDictionary(x=>x.Date);activityByDate=activityPoints.ToDictionary(x=>x.Date);
        comparisonDates=historyPoints.Select(x=>x.Date).Concat(activityPoints.Where(x=>x.State!="NoData").Select(x=>x.Date)).Distinct().Order().ToArray();
        ObservableValue Value(decimal? value)=>new(value is {} number?(double)number:null);
        Series=hasHistory?[new LineSeries<ObservableValue>{Name="监控ETF总份额（份）",Values=comparisonDates.Select(d=>Value(historyByDate.GetValueOrDefault(d)?.Shares)).ToArray(),
            GeometrySize=3,LineSmoothness=0,Stroke=new SolidColorPaint(SKColors.DodgerBlue,2),Fill=new SolidColorPaint(SKColors.DodgerBlue.WithAlpha(28))}]:[];
        MarketAmountSeries=activityPoints.Count>0?[new ColumnSeries<ObservableValue>{Name="沪深股票成交额（亿元）",Values=comparisonDates.Select(d=>Value(activityByDate.GetValueOrDefault(d)?.AmountYuan/100000000m)).ToArray(),
            Fill=new SolidColorPaint(new SKColor(100,139,180)),Stroke=null}]:[];
        Axis Axis(bool visible)=>new(){Labels=comparisonDates.Select(x=>x.ToString("yyyy-MM-dd")).ToArray(),MinLimit=-0.5,MaxLimit=Math.Max(0.5,comparisonDates.Length-0.5),MinStep=1,IsVisible=visible};
        XAxes=[Axis(false)];MarketAmountAxes=[Axis(true)];
        foreach(var axis in XAxes.Concat(MarketAmountAxes))axis.PropertyChanged+=OnComparisonAxisChanged;
        ClearComparisonHover();
    }
    private void OnComparisonAxisChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(syncingAxes || sender is not Axis changed || e.PropertyName is not (nameof(Axis.MinLimit) or nameof(Axis.MaxLimit)))return;
        syncingAxes=true;
        try {foreach(var axis in XAxes.Concat(MarketAmountAxes).Where(x=>!ReferenceEquals(x,changed))){axis.MinLimit=changed.MinLimit;axis.MaxLimit=changed.MaxLimit;}}
        finally {syncingAxes=false;}
    }
    [RelayCommand] private void ResetComparisonZoom()
    {
        foreach(var axis in XAxes.Concat(MarketAmountAxes)){axis.MinLimit=-0.5;axis.MaxLimit=Math.Max(0.5,comparisonDates.Length-0.5);}
        ClearComparisonHover();
    }
    public void HoverComparisonAt(double x)
    {
        if(!double.IsFinite(x) || x< -0.5 || x>comparisonDates.Length-0.5){ClearComparisonHover();return;}
        var index=(int)Math.Round(x,MidpointRounding.AwayFromZero);
        if(index<0 || index>=comparisonDates.Length || hoverIndex==index)return;
        hoverIndex=index;var date=comparisonDates[index];
        var history=historyByDate.GetValueOrDefault(date);var activity=activityByDate.GetValueOrDefault(date);
        string Number(decimal? value)=>value is {} number?number.ToString("N0"):"无有效数据";
        ComparisonReadout=$"{date:yyyy-MM-dd}  ·  总份额 {Number(history?.Shares)} 份  ·  净增减 {(history?.DeltaShares is {} delta?delta.ToString("+#,##0;-#,##0;0")+" 份":"不可计算")}  ·  市场成交额 {(activity?.AmountYuan is {} amount?(amount/100000000m).ToString("N2")+" 亿元":"无有效数据")}";
        RectangularSection Marker()=>new(){Xi=index,Xj=index,Stroke=new SolidColorPaint(SKColors.SlateGray,1),Fill=null};
        ShareSections=[Marker()];AmountSections=[Marker()];
    }
    public void ClearComparisonHover()
    {
        hoverIndex=-1;ShareSections=[];AmountSections=[];
        ComparisonReadout="悬停查看同日ETF份额、净增减与市场成交额；滚轮缩放，拖动平移。";
    }
}
