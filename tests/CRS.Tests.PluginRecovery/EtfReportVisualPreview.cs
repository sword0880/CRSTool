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

/// <summary>真实页面回放已保存目录；替身仅提供读取，所有采集、登记及批准调用均拒绝。</summary>
internal static class EtfReportVisualPreview
{
    public static void Render(string acceptance,string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(directory);
        using var input=JsonDocument.Parse(File.ReadAllBytes(acceptance));
        var snapshots=input.RootElement.GetProperty("Snapshots").Deserialize<EtfReportIndexSnapshot[]>()??throw new InvalidDataException("缺少已核对目录。");
        var workspace=DispatchProxy.Create<INationalTeamWorkspace,EtfReportPreviewProxy>();((EtfReportPreviewProxy)workspace).Snapshots=snapshots;
        var checks=new List<object>();
        foreach(var width in new[]{640,1040,1280})
        foreach(var scale in new[]{1.0,1.5})
        {
            var model=new EtfHoldingsViewModel(workspace,workspace,workspace,default){From=new(2026,1,1),Through=new(2026,10,27)};
            model.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if(model.Reports.Count!=6)throw new InvalidDataException("页面没有完整展示已保存六条报告。");
            var page=new EtfHoldingsPage(model,false);var window=new Window{Content=page,Width=width,Height=760,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
            try
            {
                window.Show();((TabControl)page.FindName("HoldingTabs")).SelectedIndex=1;window.UpdateLayout();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                var grid=Find<DataGrid>(page);var top=grid.TranslatePoint(new Point(),page).Y;
                if(grid.ActualHeight<360||grid.ActualWidth<width-36||top>330)throw new InvalidDataException($"目录页上方区域挤占表格或正文未铺满：宽{grid.ActualWidth}，高{grid.ActualHeight}，顶部{top}，窗口{width}。");
                var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(760*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(page);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(directory,$"etf-reports-{width}-{(int)(scale*100)}.png")))encoder.Save(stream);
                Console.WriteLine($"布局 width={width} height={grid.ActualHeight} columns={string.Join(',',grid.Columns.Select(x=>x.ActualWidth))}");
                if(grid.Columns[0].ActualWidth<109||grid.Columns[1].ActualWidth<99||grid.Columns[2].ActualWidth<259)throw new InvalidDataException("日期、类别或标题列被挤压。");
                checks.Add(new{Width=width,Density=scale,TableTop=top,TableHeight=grid.ActualHeight,Rows=model.Reports.Count,Source="CapturedOfficialResponseReplay",Installed=false});
            }
            finally{window.Close();}
        }
        File.WriteAllText(Path.Combine(directory,"layout.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS ETF目录真实页面：640/1040/1280 DIP、100/150%密度六张渲染；只读已保存官方响应，不连接后台。");
    }
    private static T Find<T>(DependencyObject root) where T:DependencyObject
    {if(root is T result)return result;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException(typeof(T).Name);}
}
public class EtfReportPreviewProxy:DispatchProxy
{
    public EtfReportIndexSnapshot[] Snapshots{get;set;}=[];
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {
        "get_CanManage"=>false,
        "ConfigurationAsync"=>Task.FromResult(Snapshots.Select(x=>new EtfConfiguration(x.Key.Market,x.Key.Ticker,"已保存官方目录回放","fixture",true,"fixture","fixture",0)).ToList()),
        "EtfReportIndexAsync"=>Task.FromResult(Snapshots.Single(x=>x.Key==(EtfKey)args![0]!)),
        _=>throw new InvalidOperationException("只读渲染禁止采集、登记或批准："+method?.Name)
    };
}
