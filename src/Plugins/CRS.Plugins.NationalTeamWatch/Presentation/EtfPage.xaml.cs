using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>加载插件XAML并绑定模型，视图不执行解析、计算或数据库操作。</summary>
public partial class EtfPage : UserControl
{
    public EtfPage(EtfViewModel viewModel)
    {
        InitializeComponent();DataContext=viewModel;
        Loaded+=async(_,_)=>await viewModel.EnsureInitializedAsync();
    }
    private void OnComparisonMouseMove(object sender,MouseEventArgs e)
    {
        if(sender is not LiveChartsCore.SkiaSharpView.WPF.CartesianChart chart || DataContext is not EtfViewModel model)return;
        var point=e.GetPosition(chart);
        if(point.X<model.ComparisonMargin.Left || point.X>chart.ActualWidth-model.ComparisonMargin.Right){model.ClearComparisonHover();return;}
        var data=chart.ScalePixelsToData(new LiveChartsCore.Drawing.LvcPointD(point.X,point.Y));
        model.HoverComparisonAt(data.X);
    }
    private void OnComparisonMouseLeave(object sender,MouseEventArgs e)
    {
        if(DataContext is EtfViewModel model)model.ClearComparisonHover();
    }
    private void OnEditConfiguration(object sender,RoutedEventArgs e)=>ConfigurationEditor.BringIntoView();
    private void OnConfigurationDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(ConfigurationGrid,origin) is DataGridRow)ConfigurationEditor.BringIntoView();
    }
}
