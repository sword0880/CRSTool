using CRS.Plugins.NationalTeamWatch.Domain;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class SouthboundViewModel
{
    public Axis[] XAxes {get;}=[MonthAxis()];
    public Axis[] YAxes {get;}=[new(){Name="累计净买入（亿港元）",MinZoomDelta=0.0001,Labeler=y=>y.ToString("N0"),TextSize=11}];
    public Margin AnnualMargin {get;}=new(100,44,28,62);
    private static readonly SKColor[] Colors=[SKColor.Parse("#2563EB"),SKColor.Parse("#D97706"),SKColor.Parse("#059669"),SKColor.Parse("#9333EA"),SKColor.Parse("#DC2626"),SKColor.Parse("#0891B2"),SKColor.Parse("#BE185D"),SKColor.Parse("#4F46E5"),SKColor.Parse("#64748B"),SKColor.Parse("#65A30D")];
    private static double[] MonthSeparators()=>Enumerable.Range(1,12).Select(m=>(double)new DateOnly(2000,m,1).DayOfYear-1).ToArray();
    private static Axis MonthAxis()=>new(){Name="月日",MinLimit=0,MaxLimit=365,MinStep=1,MinZoomDelta=1,CustomSeparators=MonthSeparators(),
        Labeler=x=>x is >=0 and <=365?new DateOnly(2000,1,1).AddDays((int)Math.Round(x)).ToString("MM-dd"):"",TextSize=11};
    private void OnZoomRangeChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is not (nameof(Axis.MinLimit) or nameof(Axis.MaxLimit)))return;
        // 放大到两个月以内时由图表按日生成刻度，避免仍只标月初而看不到单日位置。
        var axis=XAxes[0];var detailed=(axis.MaxLimit??365)-(axis.MinLimit??0)<=62;
        if(detailed&&axis.CustomSeparators is not null)axis.CustomSeparators=null;
        else if(!detailed&&axis.CustomSeparators is null)axis.CustomSeparators=MonthSeparators();
    }
    [RelayCommand]private void ResetZoom()
    {
        XAxes[0].MinLimit=0;XAxes[0].MaxLimit=365;
        var selected=Years.Where(x=>x.IsSelected).Select(x=>x.Year).ToHashSet();
        UpdateBounds(Report?.Years.Where(x=>selected.Contains(x.Year)).ToArray()??[]);
    }
    private void Rebuild()
    {
        if(Report is null)return;
        var selected=Years.Where(x=>x.IsSelected).Select(x=>x.Year).ToHashSet();
        var curves=Report.Years.Where(x=>selected.Contains(x.Year)).ToArray();
        Series=curves.Where(x=>x.Points.Any(p=>p.CumulativeNetBuyYiHkd.HasValue)).Select(CreateSeries).ToArray();
        HasValues=Series.Length>0;
        UpdateStatus(selected.Count,curves);ResetZoom();
    }
    private static ISeries CreateSeries(SouthboundYearCurve curve)
    {
        // 缩放只改变视口；点坐标与悬停金额仍直接使用同一份后台报告。
        var values=curve.Points.Where(p=>p.CumulativeNetBuyYiHkd.HasValue).Select(p=>(ObservablePoint)new SouthboundChartPoint(p,p.CumulativeNetBuyYiHkd)).ToArray();
        return new LineSeries<ObservablePoint>{Name=curve.Year+"年",Values=values,Fill=null,LineSmoothness=0,EnableNullSplitting=true,
            GeometrySize=values.Length==1?5:0,Stroke=new SolidColorPaint(Colors[(curve.Year-SouthboundAnnualComparison.EarliestYear)%Colors.Length],2),
            YToolTipLabelFormatter=p=>p.Context.DataSource is SouthboundChartPoint point?point.Tooltip:"无有效数据"};
    }
    private void UpdateStatus(int selected,SouthboundYearCurve[] curves)
    {
        var noData=curves.Where(x=>!x.Points.Any(p=>p.CumulativeNetBuyYiHkd.HasValue)).Select(x=>x.Year).ToArray();
        Status=selected==0?"请勾选要比较的年份。":!HasValues?"所选年度暂无可用累计数据，请更新数据或选择其他年份。"
            :noData.Length>0?$"{string.Join("、",noData)}年暂无可用累计数据；其余年度已显示。":"曲线下降表示当日净卖出，累计仍为正也可能有净卖出。滚轮缩放、拖动查看，悬停核对当日净额。";
        if(curves.Any(x=>x.Points.Any(p=>!p.CumulativeNetBuyYiHkd.HasValue)))Status+=" 部分年度存在基准不足、缺失或累计断点，已停止绘制受影响累计区间。";
    }
    private void UpdateBounds(SouthboundYearCurve[] curves)
    {
        // 全年视图纳入零及实际负值；用户缩放后允许放大非零区间观察下降段。
        var cumulative=curves.SelectMany(x=>x.Points).Where(x=>x.CumulativeNetBuyYiHkd.HasValue).Select(x=>(double)x.CumulativeNetBuyYiHkd!.Value).ToArray();
        YAxes[0].MinLimit=cumulative.Length==0?null:Math.Min(0,cumulative.Min());YAxes[0].MaxLimit=cumulative.Length==0?null:Math.Max(0,cumulative.Max());
        if(cumulative.Length>0&&YAxes[0].MinLimit==YAxes[0].MaxLimit){YAxes[0].MinLimit=-1;YAxes[0].MaxLimit=1;}
    }
}

/// <summary>保留后台原始日期和两种净额，悬停明确区分当日净卖出与累计仍为正。</summary>
public sealed class SouthboundChartPoint(SouthboundAnnualPoint observation,decimal? value):ObservablePoint(observation.CalendarDay,value is {} amount?(double)amount:null)
{
    public SouthboundAnnualPoint Observation {get;}=observation;
    public string Tooltip=>$"{Observation.Date:yyyy-MM-dd}\n{Amount(Observation.NetBuyYiHkd,"当日")}\n{Amount(Observation.CumulativeNetBuyYiHkd,"年内累计")}";
    private static string Amount(decimal? value,string period)=>value is not {} number?$"{period}净额：无有效数据"
        :number<0?$"{period}净卖出 {Math.Abs(number):N2} 亿港元":number>0?$"{period}净买入 {number:N2} 亿港元":$"{period}净买卖 0.00 亿港元";
}
