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
internal static class JointExitStudyVisualPreview
{
    public static void Render(string input,string output)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(output);var layouts=new List<object>();
        foreach(var state in new[]{"candidate","missing"})
        {
            var report=JsonSerializer.Deserialize<JointExitStudyReport>(File.ReadAllBytes(Path.Combine(input,state+".json")))??throw new InvalidDataException("缺少合成联合报告。");
            foreach(var width in new[]{640,1040,1280})foreach(var density in new[]{1d,1.5d})
            {
                var workspace=DispatchProxy.Create<INationalTeamWorkspace,JointExitPreviewProxy>();((JointExitPreviewProxy)workspace).Snapshot=report;
                var model=new JointExitStudyViewModel(workspace,workspace,default){AsOf=report.Input.AsOfDate.ToDateTime(TimeOnly.MinValue)};
                model.EnsureInitializedAsync().GetAwaiter().GetResult();model.ReadAsync().GetAwaiter().GetResult();model.Status="合成布局验收 · "+model.Status;
                var view=new JointExitStudyPage(model,false);var window=new Window{Content=view,Width=width,Height=700,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
                try
                {
                    window.Show();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                    var tabs=Find<TabControl>(view);if(state=="missing")tabs.SelectedIndex=3;window.UpdateLayout();
                    if(tabs.ActualHeight<500)throw new InvalidDataException("联合页头挤占正文，剩余不足500 DIP。");
                    var bitmap=new RenderTargetBitmap((int)(width*density),(int)(700*density),96*density,96*density,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(output,$"joint-{state}-{width}-{(int)(density*100)}.png")))encoder.Save(file);
                    layouts.Add(new{State=state,Width=width,Density=density,ContentHeight=tabs.ActualHeight,Installed=false,Source="SyntheticNormalizedJointStudy"});
                }
                finally{window.Close();}
            }
        }
        File.WriteAllText(Path.Combine(output,"layout.json"),JsonSerializer.Serialize(layouts,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS 联合真实控件：候选/缺失两状态、640/1040/1280 DIP和100/150%密度，共12张合成渲染。");
    }
    private static T Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T found)return found;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(KeyNotFoundException){}}throw new KeyNotFoundException();}
}
public class JointExitPreviewProxy:DispatchProxy
{
    public JointExitStudyReport Snapshot {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch
    {"get_CanManage"=>false,"ConfigurationAsync"=>Task.FromResult(new List<EtfConfiguration>{Snapshot.Input.Configuration}),"JointExitStudyAsync"=>Task.FromResult(Snapshot),_=>throw new InvalidOperationException("只读布局禁止写入和采集："+method?.Name)};
}
