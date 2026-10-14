using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using CRS.Domain;
using CRS.Domain.Performance;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace CRS.DesktopClient.Charts;

/// <summary>图表库类型只在适配层构建；金额、收益率和缺失值由后台提供。</summary>
public sealed record ChartPresentation(ISeries[] Series,Axis[] XAxes,Axis[] YAxes)
{
    public static ChartPresentation Empty=>new([],[],[]);
}
public static class DesktopChartAdapter
{
    public static ChartPresentation Income(TaxSummary summary)=>new(
        [new ColumnSeries<decimal>{Name="收入（人民币）",Values=new[]{summary.DividendCny,summary.InterestCny,summary.GainCny}}],
        [new Axis{Labels=["股息","利息","证券收益"],Name="收入项目"}],
        [new Axis{Name="人民币",Labeler=v=>v.ToString("N0")}]);
    public static ChartPresentation NetAssets(PerformanceCurve curve,string currency)=>Build(curve,[new("投资净资产",p=>p.NetAssets)],currency);
    public static ChartPresentation Comparison(PerformanceCurve curve)=>Build(curve,[new("组合累计净值（TWR）",p=>p.UnitValue),new(curve.Benchmark,p=>p.BenchmarkValue)],"起点 = 100");
    private sealed record Line(string Name,Func<PerformanceCurvePoint,decimal?> Value);
    private static ChartPresentation Build(PerformanceCurve curve,Line[] lines,string unit)=>new(
        lines.Select((line,index)=>(ISeries)new LineSeries<ObservablePoint>{Name=line.Name,GeometrySize=6,Fill=null,LineSmoothness=0,
            Stroke=new SolidColorPaint(index==0?SKColors.DodgerBlue:SKColors.OrangeRed){StrokeThickness=2},GeometryFill=new SolidColorPaint(index==0?SKColors.DodgerBlue:SKColors.OrangeRed),
            Values=curve.Points.Where(p=>line.Value(p).HasValue).Select(p=>new ObservablePoint(p.Date.DayNumber,(double)line.Value(p)!.Value)).ToArray()}).ToArray(),
        [new Axis{MinStep=1,Labeler=v=>v>=DateOnly.MinValue.DayNumber && v<=DateOnly.MaxValue.DayNumber?DateOnly.FromDayNumber((int)v).ToString("yy-MM-dd"):""}],
        [new Axis{Name=unit,MinStep=Step(curve,lines),Labeler=v=>v.ToString("N2")}]);
    private static double Step(PerformanceCurve curve,Line[] lines)
    {
        var values=lines.SelectMany(l=>curve.Points.Select(l.Value)).Where(v=>v.HasValue).Select(v=>(double)v!.Value).ToArray();
        return values.Length==0?.01:Math.Max(.01,(values.Max()-values.Min())/5);
    }
}
