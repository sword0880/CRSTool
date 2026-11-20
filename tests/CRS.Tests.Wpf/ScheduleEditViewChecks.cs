using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.Worker.Abstractions;

internal static class ScheduleEditViewChecks
{
    internal static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                foreach(var resource in new[]{"BrandPalette","SharedStyles"})app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/"+resource+".xaml")});
                Directory.CreateDirectory(directory);
                foreach(var enabled in new[]{false,true})foreach(var width in new[]{620,720})
                {
                    var model=new ScheduleEditViewModel(new(CancellationToken.None),new TaskDefinition("crs.national-team","equity-auto-sync","机构直接持股自动发现与更新","0 15 0 * * ?","China Standard Time",enabled,2,false));
                    var window=new ScheduleEditWindow(model){Width=width,Height=820};
                    try
                    {
                        window.Show();window.UpdateLayout();
                        var status=(TextBlock)window.FindName("TaskEnabledStatus");var enable=(Button)window.FindName("EnableTaskButton");var disable=(Button)window.FindName("DisableTaskButton");
                        if(status.Text!=(enabled?"已启用":"已禁用")||enable.IsEnabled==enabled||disable.IsEnabled!=enabled||enable.Command!=model.EnableTaskCommand||disable.Command!=model.DisableTaskCommand)
                            throw new Exception("任务详情状态或启用/禁用按钮绑定错误。");
                        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output=File.Create(Path.Combine(directory,$"schedule-{(enabled?"enabled":"disabled")}-{width}.png"));encoder.Save(output);
                    }
                    finally{window.Close();}
                }
                var existing = new TaskDefinition("crs.fixture", "installed", "已添加的任务", "0 0 23 * * ?",
                    "China Standard Time", true, 1, false);
                var template = existing with { JobId = "optional", Name = "待添加的任务", Revision = 0, Enabled = false };
                var readyOption = new PluginPlanOption(template);
                var savedOption = new PluginPlanOption(existing);
                var waitingOption = new PluginPlanOption("同步雪球发言", "雪球观点追踪", "待接入数据源", null, "自动同步需要授权数据源。");
                var picker = new PluginPlanPickerWindow([waitingOption, readyOption, savedOption], CancellationToken.None);
                try
                {
                    picker.Show(); picker.UpdateLayout();
                    var button = (Button)picker.FindName("ContinueButton");
                    if (picker.SelectedPlan != template || !button.IsEnabled || !Equals(button.Content, "设置计划"))
                        throw new Exception("添加计划窗口应选中可添加的插件任务。");
                    var grid = (DataGrid)picker.FindName("PluginTasks");
                    grid.SelectedItem = savedOption;
                    if (!Equals(button.Content, "编辑计划")) throw new Exception("已有计划应进入编辑。");
                    grid.SelectedItem = waitingOption;
                    if (button.IsEnabled || picker.SelectedPlan is not null ||
                        ((TextBlock)picker.FindName("SelectionDetail")).Text != waitingOption.Detail)
                        throw new Exception("待接入任务应显示原因，不能打开计划编辑。");
                }
                finally { picker.Close(); }
                var draft = new ScheduleEditWindow(new(new(CancellationToken.None), template));
                try
                {
                    draft.Show(); draft.UpdateLayout();
                    if (draft.Title != "添加计划" || ((TextBlock)draft.FindName("TaskEnabledStatus")).Text != "已禁用")
                        throw new Exception("新计划应显示创建标题和待保存的启用状态。");
                }
                finally { draft.Close(); }
                app.Shutdown();
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new Exception("任务详情启用/禁用界面验收失败。",failure);
        Console.WriteLine("PASS 添加计划选择和创建窗口、任务详情启用/禁用状态、620/720 DIP布局。");
    }
}
