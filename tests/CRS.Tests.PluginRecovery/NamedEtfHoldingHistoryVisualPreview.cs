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
internal static class NamedEtfHoldingHistoryVisualPreview
{
    public static void Render(string input,string output)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(output);var layouts=new List<object>();
        foreach(var state in new[]{"history","conflict","empty"})
        {
            var report=JsonSerializer.Deserialize<NamedEtfHoldingHistoryReport>(File.ReadAllBytes(Path.Combine(input,state+".json")))??throw new InvalidDataException("缺少合成持仓历史。");
            foreach(var width in new[]{640,1040,1280})foreach(var density in new[]{1d,1.5d})
            {
                var workspace=DispatchProxy.Create<INationalTeamWorkspace,NamedEtfHistoryPreviewProxy>();((NamedEtfHistoryPreviewProxy)workspace).Snapshot=report;
                var model=new EtfHoldingsViewModel(workspace,workspace,workspace,default);var fund=new EtfConfiguration(report.Input.Key.Market,report.Input.Key.Ticker,"合成ETF","fixture",true,"fixture","fixture",1);
                model.Funds.Add(fund);model.SelectedFund=fund;model.LoadTask.GetAwaiter().GetResult();model.History.AsOf=report.Input.AsOf.ToDateTime(TimeOnly.MinValue);model.History.ReadAsync().GetAwaiter().GetResult();
                model.History.Status="合成布局验收 · "+model.History.Status;var page=new EtfHoldingsPage(model,false);var window=new Window{Content=page,Width=width,Height=700,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
                try
                {
                    window.Show();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                    if(state=="empty")Find<TabControl>(Find<EtfHoldingHistoryView>(page)).SelectedIndex=1;window.UpdateLayout();var grid=Find<DataGrid>(page);var top=grid.TranslatePoint(new Point(),page).Y;
                    if(grid.ActualHeight<420||top>270)throw new InvalidDataException("持仓页顶部挤占正文。");
                    var bitmap=new RenderTargetBitmap((int)(width*density),(int)(700*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(page);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,$"named-{state}-{width}-{(int)(density*100)}.png")))encoder.Save(file);
                    layouts.Add(new{State=state,Width=width,Density=density,TableHeight=grid.ActualHeight,TableTop=top,Rows=model.History.Rows.Count,Installed=false,Source="SyntheticNamedEtfHoldingHistory"});
                }
                finally{window.Close();}
            }
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS 具名持仓实际页面：历史/冲突/空缺三状态，640/1040/1280 DIP及100/150%输出密度，共18张合成渲染。");
    }
    private static T Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)return value;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException();}
}
public class NamedEtfHistoryPreviewProxy:DispatchProxy
{
    public NamedEtfHoldingHistoryReport Snapshot{get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>false,"EtfHoldingHistoryAsync"=>Task.FromResult(Snapshot),"EtfReportIndexAsync"=>Task.FromResult(new EtfReportIndexSnapshot(Snapshot.Input.Key,"NotSearched",null,null,null,null,null,[],EtfReportIndexSnapshot.Version)),_=>throw new InvalidOperationException("只读渲染禁止采集或批准："+method?.Name)};
}
