using System.Windows;
using System.Windows.Controls;
namespace CRS.Plugins.NationalTeamWatch.Presentation;
public partial class MarketBreadthView:UserControl
{
    public MarketBreadthView()=>InitializeComponent();
    private void ChooseOriginal(object sender,RoutedEventArgs e)=>Choose(false);
    private void ChooseNormalized(object sender,RoutedEventArgs e)=>Choose(true);
    private void Choose(bool normalized)
    {
        if(DataContext is not MarketBreadthViewModel model||!model.CanEdit)return;
        var dialog=new Microsoft.Win32.OpenFileDialog{CheckFileExists=true,Multiselect=false,Filter=normalized?"标准化广度档案|*.json":"原始资料|*.*"};
        if(dialog.ShowDialog(Window.GetWindow(this))==true){if(normalized)model.NormalizedPath=dialog.FileName;else model.OriginalPath=dialog.FileName;}
    }
    private async void ExportOriginal(object sender,RoutedEventArgs e)=>await Export(true);
    private async void ExportNormalized(object sender,RoutedEventArgs e)=>await Export(false);
    private async Task Export(bool original)
    {
        if(DataContext is not MarketBreadthViewModel model||model.Selected is null)return;
        var dialog=new Microsoft.Win32.SaveFileDialog{FileName=original?"breadth-original.bin":"breadth-normalized.json",Filter=original?"原始字节|*.bin":"标准化广度档案|*.json",OverwritePrompt=true};
        if(dialog.ShowDialog(Window.GetWindow(this))==true)await model.ExportAsync(original,dialog.FileName);
    }
}
