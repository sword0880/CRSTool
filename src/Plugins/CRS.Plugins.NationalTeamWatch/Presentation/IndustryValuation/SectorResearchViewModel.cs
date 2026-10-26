using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Plugins.NationalTeamWatch.Domain;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>只把后端已计算的曲线和判断投影成图表，不在界面计算收益、估值分位或机会条件。</summary>
public partial class SectorResearchViewModel : ObservableObject
{
    public IReadOnlyList<SectorSelection> Rows {get;}
    public IReadOnlyList<string> Metrics {get;}=["走势（共同起点100）","PE历史分位","PB历史分位"];
    public string Caption {get;}
    public string Provenance {get;}
    [ObservableProperty] private string metric="走势（共同起点100）";
    [ObservableProperty] private ISeries[] series=[];
    [ObservableProperty] private Axis[] xAxes=[];
    [ObservableProperty] private Axis[] yAxes=[];
    [ObservableProperty] private string status="";
    public SectorResearchViewModel(SectorStudyReport report,string provenance)
    {
        Provenance=provenance;Caption=$"共同起点 {report.BaseDate:yyyy-MM-dd} · 截至 {report.Through:yyyy-MM-dd}";
        Rows=report.Rows.Select(x=>new SectorSelection(x,Rebuild)).ToArray();Rebuild();
    }
    partial void OnMetricChanged(string value)=>Rebuild();
    private void Rebuild()
    {
        var selected=Rows.Where(x=>x.IsVisible).ToArray();var dates=Rows.FirstOrDefault()?.Row.Curve.Select(x=>x.Date).ToArray()??[];
        Series=selected.Select(x=>(ISeries)new LineSeries<ObservablePoint>
        {
            Name=x.Row.Name,GeometrySize=0,Fill=null,LineSmoothness=0,EnableNullSplitting=true,
            Values=x.Row.Curve.Select((p,i)=>new ObservablePoint(i,Value(p) is {} value?(double)value:null)).ToArray()
        }).ToArray();
        XAxes=[new(){Labels=dates.Select(x=>x.ToString("MM-dd")).ToArray(),MinStep=1}];
        YAxes=[new(){Name=Metric,MinLimit=Metric==Metrics[0]?null:0,MaxLimit=Metric==Metrics[0]?null:100}];
        Status=Rows.Count==0?"暂无可用行业行情与估值资料。":selected.Length==0?"请选择要对比的行业。":"价格低不等于低估；分位只比较本行业自身历史，资料不足保持空白。";
    }
    private decimal? Value(SectorChartPoint point)=>Metric==Metrics[1]?point.PePercentile:Metric==Metrics[2]?point.PbPercentile:point.NormalizedClose;
}
/// <summary>选择状态只影响展示；中文状态保留后端的未知、基本面不足和盈利恶化差异。</summary>
public partial class SectorSelection(SectorStudyRow row,Action changed):ObservableObject
{
    public SectorStudyRow Row {get;}=row;
    [ObservableProperty] private bool isVisible=true;
    partial void OnIsVisibleChanged(bool value)=>changed();
    public string State=>Row.OpportunityState switch
    {
        "NeedsAdversarialAssessment"=>"数据齐备，仍待反方评估", "IndependentQualityEvidenceUnavailable"=>"独立基本面证据未齐备", "ValueTrapRisk"=>"盈利恶化，核查低估陷阱", "FundamentalsUnavailable"=>"基本面不足，无法判断",
        "NoLowValuationCondition"=>"未满足历史低估值条件", "PriceDataIncomplete"=>"走势数据不完整", _=>"估值资料不足，无法判断"
    };
}
