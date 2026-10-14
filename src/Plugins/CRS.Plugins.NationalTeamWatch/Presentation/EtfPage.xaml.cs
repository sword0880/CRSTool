using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
/// <summary>加载插件XAML并绑定模型，视图不执行解析、计算或数据库操作。</summary>
public partial class EtfPage : UserControl
{
    public EtfPage(EtfViewModel viewModel) {InitializeComponent();DataContext=viewModel;}
}
