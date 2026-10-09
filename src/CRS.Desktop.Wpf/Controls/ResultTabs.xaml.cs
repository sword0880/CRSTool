using System.Collections;
using System.Windows;
using System.Windows.Controls;
namespace CRS.DesktopClient.Controls;
/// <summary>复用后台底稿投影，不复制业务数据或计算逻辑。</summary>
public partial class ResultTabs : UserControl
{
    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(nameof(Sections), typeof(IEnumerable), typeof(ResultTabs));
    public IEnumerable? Sections { get => (IEnumerable?)GetValue(SectionsProperty); set => SetValue(SectionsProperty, value); }
    public ResultTabs() => InitializeComponent();
}
