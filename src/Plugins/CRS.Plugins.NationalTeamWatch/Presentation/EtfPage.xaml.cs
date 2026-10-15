using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>加载插件XAML并绑定模型，视图不执行解析、计算或数据库操作。</summary>
public partial class EtfPage : UserControl
{
    public EtfPage(EtfViewModel viewModel) {InitializeComponent();DataContext=viewModel;}
    private void OnEditConfiguration(object sender,RoutedEventArgs e)=>ConfigurationEditor.BringIntoView();
    private void OnConfigurationDoubleClick(object sender,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(ConfigurationGrid,origin) is DataGridRow)ConfigurationEditor.BringIntoView();
    }
    /// <summary>表格到达上下边界后将滚轮交给整页，避免鼠标停在表格上时无法查看下方图表。</summary>
    private void OnTableMouseWheel(object sender,MouseWheelEventArgs e)
    {
        if(sender is not DataGrid grid || FindScroll(grid) is not { } scroll)return;
        if(e.Delta>0 && scroll.VerticalOffset<=0 || e.Delta<0 && scroll.VerticalOffset>=scroll.ScrollableHeight)
        {
            PageScroll.ScrollToVerticalOffset(PageScroll.VerticalOffset-e.Delta);
            e.Handled=true;
        }
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if(parent is ScrollViewer scroll)return scroll;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
            if(FindScroll(VisualTreeHelper.GetChild(parent,index)) is { } child)return child;
        return null;
    }
}
