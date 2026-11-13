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

internal static class MarketBreadthVisualPreview
{
    public static void Render(string input,string output)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(output);var layouts=new List<object>();
        foreach(var state in new[]{"pending","conflict","selected"})foreach(var width in new[]{640,1040})foreach(var density in new[]{1d,1.5d})
        {
            var report=JsonSerializer.Deserialize<MarketBreadthHistory>(File.ReadAllBytes(Path.Combine(input,state+".json")))??throw new InvalidDataException("合成广度档案缺失。");
            var workspace=DispatchProxy.Create<IMarketCycleWorkspace,MarketBreadthPreviewProxy>();((MarketBreadthPreviewProxy)workspace).Snapshot=report;
            var model=new MarketBreadthViewModel(workspace,default){Date=report.From.ToDateTime(TimeOnly.MinValue)};model.QueryCommand.ExecuteAsync(null).GetAwaiter().GetResult();model.Selected=model.Rows.FirstOrDefault();model.Status="合成档案布局验收 · "+model.Status;
            var view=new MarketBreadthView{DataContext=model};var window=new Window{Content=view,Width=width,Height=700,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
            try
            {
                window.Show();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                var grid=Find<DataGrid>(view);if(grid.ActualHeight<480||grid.Columns.Take(4).Sum(x=>x.ActualWidth)>grid.ActualWidth-15)throw new InvalidDataException("广度档案主要列或正文空间不满足窄屏要求。");
                Save(view,Path.Combine(output,$"breadth-{state}-{width}-{(int)(density*100)}.png"),width,density);layouts.Add(new{State=state,Width=width,Density=density,TableHeight=grid.ActualHeight,Installed=false,Source="SyntheticBreadthWorkflow"});
                if(state=="pending"&&density==1)
                {
                    var editor=Find<Expander>(view);editor.IsExpanded=true;
                    var expandedFrame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(400)};timer.Tick+=(_,_)=>{timer.Stop();expandedFrame.Continue=false;};timer.Start();Dispatcher.PushFrame(expandedFrame);window.UpdateLayout();
                    if(editor.ActualHeight<120)throw new InvalidDataException("核验编辑区未实际展开。");
                    Save(view,Path.Combine(output,$"breadth-edit-{width}.png"),width,density);
                }
            }
            finally{window.Close();}
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS 广度控件：12张三状态/两宽度/两密度截图及2张展开核验区截图。");
    }
    private static void Save(FrameworkElement view,string path,int width,double density)
    {var bitmap=new RenderTargetBitmap((int)(width*density),(int)(700*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    private static T Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T found)return found;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException();}
}
public class MarketBreadthPreviewProxy:DispatchProxy
{
    public MarketBreadthHistory Snapshot {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>true,"MarketBreadthHistoryAsync"=>Task.FromResult(Snapshot),_=>throw new InvalidOperationException("合成布局不执行资料登记、审核或文件提交："+method?.Name)};
}
