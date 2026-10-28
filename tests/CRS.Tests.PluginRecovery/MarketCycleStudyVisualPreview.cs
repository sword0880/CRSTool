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

internal static class MarketCycleStudyVisualPreview
{
    public static void Render(string input,string output)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(output);var layouts=new List<object>();
        foreach(var state in new[]{"preview","missing"})
        {
            var report=JsonSerializer.Deserialize<MarketCycleStudyReport>(File.ReadAllBytes(Path.Combine(input,state+".json")))??throw new InvalidDataException("缺少合成周期报告。");
            foreach(var width in new[]{640,1040,1280})foreach(var density in new[]{1d,1.5d})
            {
                var workspace=DispatchProxy.Create<IMarketCycleWorkspace,MarketCycleStudyPreviewProxy>();((MarketCycleStudyPreviewProxy)workspace).Snapshot=report;
                var model=new MarketCycleStudyViewModel(workspace,default){AsOf=report.Input.AsOfDate.ToDateTime(TimeOnly.MinValue)};model.ReadAsync().GetAwaiter().GetResult();model.Status="合成周期资料布局验收 · "+model.Status;
                var view=new MarketCycleStudyPage(model);var window=new Window{Content=view,Width=width,Height=700,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
                try
                {
                    window.Show();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                    var tabs=Find<TabControl>(view);tabs.SelectedIndex=state=="preview"?1:2;window.UpdateLayout();
                    if(tabs.ActualHeight<400)throw new InvalidDataException("周期页头挤占正文，剩余不足400 DIP。");
                    var bitmap=new RenderTargetBitmap((int)(width*density),(int)(700*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(output,$"cycle-{state}-{width}-{(int)(density*100)}.png")))encoder.Save(file);
                    layouts.Add(new{State=state,Width=width,Density=density,ContentHeight=tabs.ActualHeight,Installed=false,Source="SyntheticCycleStudy"});
                }
                finally{window.Close();}
            }
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS 周期真实控件：完整/缺失两状态、640/1040/1280 DIP与100/150%密度，共12张合成渲染。");
    }
    private static T Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T found)return found;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException();}
}
public class MarketCycleStudyPreviewProxy:DispatchProxy
{
    public MarketCycleStudyReport Snapshot {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>false,"MarketCycleStudyAsync"=>Task.FromResult(Snapshot),_=>throw new InvalidOperationException("只读布局禁止采集、写入及通知："+method?.Name)};
}
