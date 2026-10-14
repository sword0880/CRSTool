using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using CRS.DesktopClient.ViewModels;

namespace CRS.DesktopClient.Views.Pages;
/// <summary>视图只转交密码和加载事件，不读取资料文件或执行绩效规则。</summary>
public partial class PerformancePage : Page
{
    private readonly PerformanceViewModel model;
    public PerformancePage(ShellViewModel shell)
    {
        InitializeComponent();model=shell.Performance;DataContext=model;
        Unloaded+=(_,_)=>model.HideAccountDetailsCommand.Execute(null);
        Loaded+=async(_,_)=>
        {
            // 导航页的独立视觉树找不到 NavigationView 祖先；显式绑定所属窗口的视口。
            var owner=Window.GetWindow(this)??System.Windows.Application.Current.MainWindow;
            if(owner?.FindName("Navigation") is FrameworkElement viewport)
                ViewportGrid.SetBinding(HeightProperty,new Binding(nameof(ActualHeight)){Source=viewport,Converter=new Converters.NavigationViewportHeightConverter()});
            if(model.RefreshCommand.CanExecute(null))await model.RefreshCommand.ExecuteAsync(null);
        };
    }
}
