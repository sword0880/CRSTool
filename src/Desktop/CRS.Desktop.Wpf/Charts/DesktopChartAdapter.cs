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

    public static ChartPresentation AnnualAssets(PerformanceAnnualAssets[] years, int currentYear, Func<string, string> brokerName)
    {
        var covered = years.SelectMany(y => y.Brokers).Where(b => b.NetAssetsCny.HasValue).ToArray();
        if (covered.Length == 0) return ChartPresentation.Empty;
        var brokers = covered.Select(b => b.Broker).Distinct().Order(StringComparer.Ordinal);

        var series = new List<ISeries>();
        foreach (var broker in brokers)
        {
            var values = years.Select(y => y.Brokers.SingleOrDefault(b => b.Broker == broker)).ToArray();
            series.Add(new StackedColumnSeries<decimal?>
            {
                Name = brokerName(broker),
                Fill = new SolidColorPaint(BrokerColor(broker)),
                MaxBarWidth = 64,
                Values = values.Select(b => b?.NetAssetsCny).ToArray(),
                XToolTipLabelFormatter = point => $"{years[point.Index].Date:yyyy-MM-dd}",
                YToolTipLabelFormatter = point =>
                {
                    var value = values[point.Index]!;
                    var native = string.Join(" / ", value.NativeAmounts.Select(a => $"{a.Currency} {a.Amount:N2}"));
                    return $"{value.NetAssetsCny:N2} 元 · {native}";
                }
            });
        }

        if (years.Any(y => y.TotalCny.HasValue))
        {
            var ink = SKColor.Parse("#372C35");
            series.Add(new LineSeries<decimal?>
            {
                Name = "总净资产",
                Values = years.Select(y => y.TotalCny).ToArray(),
                EnableNullSplitting = true,
                Fill = null,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(ink) { StrokeThickness = 2.5f },
                GeometrySize = 9,
                GeometryFill = new SolidColorPaint(ink),
                GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2 },
                ZIndex = 10,
                XToolTipLabelFormatter = point => $"{years[point.Index].Date:yyyy-MM-dd}",
                YToolTipLabelFormatter = point => $"{years[point.Index].TotalCny:N2} 元"
            });
        }

        // 柱高以零为基线，自动缩放时也要保留零值和负净资产。
        var hasNegative = covered.Any(b => b.NetAssetsCny < 0);
        var hasPositive = covered.Any(b => b.NetAssetsCny > 0);
        return new(series.ToArray(),
            [new Axis { MinStep = 1, MinLimit = -0.5, MaxLimit = years.Length - 0.5, Labels = years.Select(y => y.Year == currentYear && y.Date.HasValue
                ? $"{y.Year} · {y.Date:MM-dd}" : y.Year.ToString()).ToArray() }],
            [new Axis { MinLimit = hasNegative ? null : 0, MaxLimit = hasPositive ? null : (hasNegative ? 0 : 1),
                MinStep = 0.01, Labeler = value => value.ToString("#,0.##") }]);
    }

    private static SKColor BrokerColor(string broker) => broker switch
    {
        "FUTU" => SKColor.Parse("#C56342"),
        "IBKR" => SKColor.Parse("#337F8D"),
        _ => SKColor.Parse("#6E62A2")
    };

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
