using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

namespace CRS.Tests.PluginRecovery;

internal static class EquityDirectionVisualPreview
{
    public static void Render(string input,string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(directory);
        var snapshot=JsonSerializer.Deserialize<EquityDirectionReport>(File.ReadAllBytes(input))??throw new InvalidDataException("缺少合成方向报告。");
        var workspace=DispatchProxy.Create<IDirectEquityWorkspace,EquityDirectionPreviewProxy>();((EquityDirectionPreviewProxy)workspace).Snapshot=snapshot;
        var layouts=new List<object>();
        foreach(var width in new[]{640,1040,1280})
        foreach(var density in new[]{1d,1.5d})
        {
            var model=new EquityViewModel(workspace,default);model.LoadDirectionAsync().GetAwaiter().GetResult();
            model.DirectionStatus="合成样本布局验收 · "+model.DirectionStatus;
            var view=new EquityDirectionView{DataContext=model};var window=new Window{Content=view,Width=width,Height=620,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
            try
            {
                window.Show();Pump();var grid=All(view).OfType<DataGrid>().Single(x=>ReferenceEquals(x.ItemsSource,model.DirectionGroups));
                if(grid.ActualHeight<350||grid.ActualWidth<width-20)throw new InvalidDataException("方向报告表格被页头挤压或正文未占满。");
                if(grid.Columns.Take(5).Sum(x=>x.ActualWidth)>grid.ActualWidth-15)throw new InvalidDataException("窄视口中机构、行业、性质、市值和样本权重须优先完整展示。");
                Save(view,$"direction-{width}-{(int)(density*100)}.png");
                if(width==640&&density==1d)
                {
                    var tabs=All(view).OfType<TabControl>().First();tabs.SelectedIndex=1;Pump();model.SelectedDirectionPosition=model.DirectionPositions.First();All(view).OfType<Expander>().Single().IsExpanded=true;Pump();Save(view,"direction-review-640.png");
                    tabs.SelectedIndex=2;Pump();Save(view,"direction-gaps-640.png");
                }
                layouts.Add(new{Width=width,Density=density,TableHeight=grid.ActualHeight,Rows=model.DirectionGroups.Count,Source="SyntheticReviewedEquityFixtures",Installed=false});
                void Save(FrameworkElement element,string name)
                {var bitmap=new RenderTargetBitmap((int)(width*density),(int)(620*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(directory,name));encoder.Save(stream);}
            }
            finally{window.Close();}
        }
        File.WriteAllText(Path.Combine(directory,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS 真实方向视图：640/1040/1280 DIP、100/150%密度六张报告及两张窄屏核验/缺口图；仅合成样本，全部写入禁止。");
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in All(VisualTreeHelper.GetChild(root,i)))yield return child;}
}
public class EquityDirectionPreviewProxy:DispatchProxy
{
    public EquityDirectionReport Snapshot {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>false,"EquityDirectionAsync"=>Task.FromResult(Snapshot),_=>throw new InvalidOperationException("只读渲染禁止审核、采集或写入："+method?.Name)};
}
