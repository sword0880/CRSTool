using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

internal static class SouthboundUiChecks
{
    public static void Run(string file,string screenshot)
    {
        var report=JsonSerializer.Deserialize<SouthboundAnnualReport>(File.ReadAllText(file))??throw new InvalidDataException("年度报告缺失。");
        Exception? failure=null;var passed=0;
        var thread=new Thread(()=>
        {
            Window? window=null;
            void Check(bool value,string name){if(!value)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 南向折线界面："+name);}
            try
            {
                // 隔离窗口也使用真实UI调度上下文，异步取消的晚到响应不能落到线程池更新控件。
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var plugin=new NationalTeamPluginEntry().CreateSession(null!);
                try
                {
                    plugin.StartAsync(default).GetAwaiter().GetResult();
                    var root=(NationalTeamWorkspacePage)((CRS.Plugin.Wpf.IPluginWpfSession)plugin).GetPages().Single().CreateView();
                    root.OpenRoute("southbound-annual");Check(root.SelectedRouteKey=="southbound-annual","真实插件目录可创建并打开南向年度折线页");
                }
                finally{plugin.StopAsync(default).GetAwaiter().GetResult();plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();}
                var proxy=new Workspace(report);using var life=new CancellationTokenSource();var model=new SouthboundViewModel(proxy,life.Token);
                window=new(){Title="南向年度折线 · 真实公开数据隔离验证",Content=new SouthboundPage(model,false),Width=1100,Height=790,MaxHeight=SystemParameters.WorkArea.Height,Background=SystemColors.WindowBrush};
                window.Show();Pump(model.QueryCommand.ExecuteAsync(null));
                var visible=report.Years.Where(y=>model.Years.Any(x=>x.Year==y.Year&&x.IsSelected)&&y.Points.Any(p=>p.BuyYiHkd.HasValue)).ToArray();
                Check(model.HasValues&&model.Series.Length==visible.Length&&visible.Length>=2,"不同年度在同一图中各显示一条折线");
                Check(model.Series.All(x=>x is LineSeries<ObservablePoint> {Fill:null,LineSmoothness:0}),"仅折线，不增加柱图、面积或平滑后的虚构波动");
                foreach(var curve in visible)
                {
                    var series=(LineSeries<ObservablePoint>)model.Series.Single(x=>x.Name==curve.Year+"年");
                    var points=series.Values!.ToArray();
                    Check(points.Length==curve.Points.Count(p=>p.BuyYiHkd.HasValue)&&points.All(p=>p.X is >=0 and <=365&&p.Y.HasValue),curve.Year+"年坐标保留月日位置，无补零或未来延长");
                }
                var first=model.Years.First(x=>visible.Any(y=>y.Year==x.Year));first.IsSelected=false;Check(model.Series.Length==visible.Length-1,"勾选年份即时改变可见曲线");first.IsSelected=true;
                foreach(var year in model.Years)year.IsSelected=false;Check(!model.HasValues&&model.Series.Length==0&&model.Status.Contains("勾选",StringComparison.Ordinal),"未选择年份时不显示零金额坐标");
                foreach(var year in model.Years)year.IsSelected=year.Year>=CRS.Timing.BeijingClock.Now.Year-4;
                Pump();Pump();
                var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);using(var output=File.Create(screenshot)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(output);}
                window.Width=700;Pump();Check(window.Content is FrameworkElement {ActualWidth:>600},"较窄窗口保持图表区域可见");
                proxy.Mode="Failure";Pump(model.QueryCommand.ExecuteAsync(null));Check(model.Report is null&&model.Series.Length==0,"读取失败清除旧曲线");
                proxy.Mode="Wait";var old=model.QueryCommand.ExecuteAsync(null);Pump();model.Cancel();proxy.Pending.SetResult(report);Pump(old);
                Check(model.Report is null&&model.Series.Length==0,"取消后晚到报告不能重新显示旧数据");
                proxy.Mode="Normal";proxy.Manage=false;Check(!model.UpdateCommand.CanExecute(null),"只读用户不能提交后台采集");
                File.WriteAllText(Path.ChangeExtension(screenshot,".validation.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,report.CaptureId,Years=visible.Select(y=>y.Year)},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception error){failure=error;}
            finally{window?.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
        Console.WriteLine($"PASS 南向折线界面 {passed} 项；正式服务尚未部署。");
    }
    private static void Pump(Task? task=null)
    {
        var watch=Stopwatch.StartNew();do{var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("年度折线检查超时。");}while(task is not null?!task.IsCompleted:watch.Elapsed<TimeSpan.FromMilliseconds(1400));task?.GetAwaiter().GetResult();
    }
    private sealed class Workspace(SouthboundAnnualReport report):ISouthboundWorkspace
    {
        public bool Manage=true;public bool CanManage=>Manage;public string Mode="Normal";
        public TaskCompletionSource<SouthboundAnnualReport> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SouthboundAnnualReport> SouthboundAnnualAsync(int firstYear,int lastYear,CancellationToken token)=>Mode=="Failure"?Task.FromException<SouthboundAnnualReport>(new IOException("合成读取失败")):Mode=="Wait"?Pending.Task:Task.FromResult(report);
        public Task<SouthboundCommandReceipt> QueueSouthboundAsync(CancellationToken token)=>Task.FromResult(new SouthboundCommandReceipt(Guid.NewGuid(),"Pending","Accepted"));
        public Task<SouthboundCommandReceipt> SouthboundStatusAsync(Guid requestId,CancellationToken token)=>Task.FromResult(new SouthboundCommandReceipt(requestId,"Completed","Succeeded"));
    }
}
