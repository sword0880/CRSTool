using System.Windows.Controls;

namespace CRS.DesktopClient.Views.Pages.Performance;

/// <summary>只承载数据绑定，不访问市场服务、账户资料或持久化层。</summary>
public partial class PerformanceHistoryView : UserControl
{
    public PerformanceHistoryView() => InitializeComponent();
}
