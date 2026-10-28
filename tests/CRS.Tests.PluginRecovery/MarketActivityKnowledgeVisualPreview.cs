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

internal static class MarketActivityKnowledgeVisualPreview
{
    public static void Render(string input,string output)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(output);
        var report=JsonSerializer.Deserialize<MarketActivityKnowledgeReport>(File.ReadAllBytes(input))??throw new InvalidDataException("缺少合成知识截面。");
        var workspace=DispatchProxy.Create<IMarketCycleWorkspace,MarketActivityKnowledgePreviewProxy>();((MarketActivityKnowledgePreviewProxy)workspace).Snapshot=report;
        var layouts=new List<object>();
        foreach(var width in new[]{640,1040,1280})foreach(var density in new[]{1d,1.5d})
        {
            var model=new MarketActivityKnowledgeViewModel(workspace,default){From=report.From.ToDateTime(TimeOnly.MinValue),Through=report.Through.ToDateTime(TimeOnly.MinValue)};
            model.ReadAsync().GetAwaiter().GetResult();model.Status="合成时点资料布局验收 · "+model.Status;
            var view=new MarketActivityKnowledgeView{DataContext=model};var window=new Window{Content=view,Width=width,Height=700,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
            try
            {
                window.Show();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                var grid=FindGrid(view);if(grid.ActualHeight<450||grid.Columns.Take(5).Sum(x=>x.ActualWidth)>grid.ActualWidth-15)throw new InvalidDataException("知识时点表格被页头挤占或主要时间列在窄屏不可见。");
                var bitmap=new RenderTargetBitmap((int)(width*density),(int)(700*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(output,$"activity-knowledge-{width}-{(int)(density*100)}.png")))encoder.Save(file);
                layouts.Add(new{Width=width,Density=density,TableHeight=grid.ActualHeight,Rows=model.Rows.Count,Installed=false,Source="SyntheticKnowledgeTimeline"});
            }
            finally{window.Close();}
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS 成交知识真实控件：640/1040/1280 DIP、100/150%密度六张合成资料渲染；全部写入禁止。");
    }
    private static DataGrid FindGrid(DependencyObject root)
    {if(root is DataGrid grid)return grid;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return FindGrid(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException();}
}
public class MarketActivityKnowledgePreviewProxy:DispatchProxy
{
    public MarketActivityKnowledgeReport Snapshot {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>false,"MarketActivityKnowledgeAsync"=>Task.FromResult(Snapshot),_=>throw new InvalidOperationException("只读渲染禁止提交审核或采集："+method?.Name)};
}
