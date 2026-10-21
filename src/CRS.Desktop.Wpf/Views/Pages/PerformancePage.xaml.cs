using System.Windows.Controls;
using System.Windows;
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
            // Grid 自动接受宿主布局尺寸；不依赖另一个控件的名称/尺寸。
            if(model.RefreshCommand.CanExecute(null))await model.RefreshCommand.ExecuteAsync(null);
        };
    }
}
