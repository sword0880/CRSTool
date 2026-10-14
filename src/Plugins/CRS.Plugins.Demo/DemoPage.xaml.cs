using System.Windows.Controls;
using CRS.Plugin.Abstractions;
using CRS.Plugin.Wpf;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.Demo;
/// <summary>第二个独立WPF插件的资源验证页面；曲线是合成样例，不是市场数据。</summary>
public partial class DemoPage : UserControl
{
    public DemoPage() {InitializeComponent();Chart.Series=[new LineSeries<double>{Values=new double[]{1,3,2,4},Fill=null,Stroke=new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(SkiaSharp.SKColors.DodgerBlue){StrokeThickness=2}}];}
}
public sealed class DemoEntry : ICrsPluginEntry {public IPluginSession CreateSession(IPluginHostContext context)=>new DemoSession();}
internal sealed class DemoSession : IPluginSession,IPluginWpfSession
{
    public ValueTask StartAsync(CancellationToken cancellationToken)=>ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken)=>ValueTask.CompletedTask;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    public IReadOnlyList<PluginPageContribution> GetPages()=>[new("resources","插件资源演示",()=>new DemoPage())];
}
