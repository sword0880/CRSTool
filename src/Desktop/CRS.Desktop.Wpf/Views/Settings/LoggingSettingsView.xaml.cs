using System.Windows.Controls;
namespace CRS.DesktopClient.Views.Settings;

/// <summary>日志设置子视图：无单独保存、无密钥缓存，继承 SettingsViewModel。</summary>
public partial class LoggingSettingsView : UserControl
{
    public LoggingSettingsView() => InitializeComponent();
}
