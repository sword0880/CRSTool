using System.Windows.Controls;

namespace CRS.DesktopClient.Views.Pages.Performance;

/// <summary>共享原 PerformanceViewModel 的绩效子视图，生命周期和计算操作仍由父页面管理。</summary>
public partial class PerformanceManualEntryView : UserControl
{
    public PerformanceManualEntryView() => InitializeComponent();
}
