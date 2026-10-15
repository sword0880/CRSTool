using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Worker.Ipc;

/// <summary>配置界面通过测试服务真实管道新增和修改，所有写入均属于隔离测试目录。</summary>
internal static class EtfConfigurationUiChecks
{
    internal static void Run(BackgroundPipeClient client,string screenshot)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var application=new System.Windows.Application();
            application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            using var lifetime=new CancellationTokenSource();
            var workspace=new WorkerEtfWorkspace(client);
            var model=new EtfViewModel(workspace,lifetime.Token);var page=new EtfPage(model);
            var window=new Window{Title="ETF配置隔离验证",Content=page,Width=1180,Height=820};
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    await model.RefreshCommand.ExecuteAsync(null);
                    if(model.Configurations.Count<24)throw new InvalidOperationException("配置目录没有加载完整。");
                    var tabs=Find<TabControl>(page)!;tabs.SelectedIndex=1;window.UpdateLayout();
                    model.ConfigurationSearch="510500";
                    if(model.ConfigurationView.Cast<object>().Count()!=1)throw new InvalidOperationException("代码搜索未过滤目录。");
                    model.SelectedConfiguration=model.Configurations.Single(x=>x.Ticker=="510500");
                    if(model.SecurityName.Length==0 || model.IndexGroup!="CSI500" || model.IsNewConfiguration)throw new InvalidOperationException("选中ETF未带入编辑字段。");
                    model.SecurityName="合成UI修改名称";model.IndexGroup="UI-TEST";model.Reason="合成配置界面回归";model.MonitorEnabled=false;
                    await model.SaveConfigurationCommand.ExecuteAsync(null);
                    if(model.Configurations.Single(x=>x.Ticker=="510500").Name!="合成UI修改名称" || !model.Status.Contains("已保存"))throw new InvalidOperationException("界面修改没有保存到后台服务。");
                    model.NewConfigurationCommand.Execute(null);
                    if(!model.IsNewConfiguration || model.Ticker.Length!=0 || model.MonitorEnabled)throw new InvalidOperationException("新增模式没有清空固定标识或错误自动启用监控。");
                    model.Ticker="510903";model.SecurityName="合成UI新增ETF";model.IndexGroup="UI-TEST";model.Reason="合成新增验证";
                    await model.SaveConfigurationCommand.ExecuteAsync(null);
                    if(!model.Configurations.Any(x=>x.Ticker=="510903" && !x.Active))throw new InvalidOperationException("界面新增候选未持久保存。");
                    // 在隔离服务启用20只后，再经真实界面启用第21只，验证20仅限制视口而非业务数量。
                    foreach(var entry in model.Configurations.Where(x=>!x.Active).Take(20-model.Configurations.Count(x=>x.Active)).ToArray())
                        await workspace.SaveConfigurationAsync(new(entry.Market,entry.Ticker),entry.Name,entry.IndexGroup,true,"合成超过视口验证",entry.Revision,default);
                    await model.RefreshConfigurationCommand.ExecuteAsync(null);
                    model.SelectedConfiguration=model.Configurations.First(x=>!x.Active);model.MonitorEnabled=true;model.Reason="合成界面启用第21只";
                    await model.SaveConfigurationCommand.ExecuteAsync(null);
                    if(model.Rows.Count!=21 || model.ConfigurationCount.Contains("/20"))throw new InvalidOperationException("界面仍将20行视口当作监控上限。");
                    model.ConfigurationSearch="";tabs.SelectedIndex=0;window.UpdateLayout();
                    var overview=(DataGrid)page.FindName("OverviewGrid");overview.ScrollIntoView(model.Rows.Last());
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                    var scroll=Find<ScrollViewer>(overview)!;
                    if(overview.Items.Count!=21 || scroll.ScrollableHeight<=0 || scroll.VerticalOffset<=0)throw new InvalidOperationException("第21只ETF未完整保留或无法通过表格滚动访问。");
                    var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(screenshot);encoder.Save(output);
                    Console.WriteLine("PASS ETF配置界面：完整目录、搜索、修改、新增、第21只启用及超过视口后的真实滚动。");
                }
                catch(Exception error){failure=error;}
                finally {lifetime.Cancel();window.Close();}
            };
            application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("ETF配置界面验证失败。",failure);
    }
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)return match;
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)if(Find<T>(VisualTreeHelper.GetChild(parent,index)) is { } child)return child;
        return null;
    }
}
