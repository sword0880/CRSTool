using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>仅通过正式业务管道读取指数报告并渲染真实视图，不注入资料、提交采集或更改计划。</summary>
internal static class IndexInstalledUiChecks
{
    public static void Run(DateOnly from,DateOnly through,string screenshot)
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            Window? window=null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                using var lifetime=new CancellationTokenSource();var model=new IndexResearchViewModel(new WorkerEtfWorkspace(),lifetime.Token){From=from.ToDateTime(TimeOnly.MinValue),Through=through.ToDateTime(TimeOnly.MinValue)};
                window=new(){Title="正式后台指数缺口与准入界面验收",Content=new IndexResearchPage(model,false),Width=1280,Height=1000,MaxHeight=SystemParameters.WorkArea.Height,Background=SystemColors.WindowBrush};window.Show();
                void Tick()
                {
                    var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                    timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
                }
                var query=model.QueryCommand.ExecuteAsync(null);var timer=Stopwatch.StartNew();while(!query.IsCompleted&&timer.Elapsed<TimeSpan.FromSeconds(45))Tick();query.GetAwaiter().GetResult();
                if(model.Rows.Count!=11||model.Rows.Any(x=>x.Row.AcceptedPriceDays!=0||x.Row.PublishedPb.HasValue)||model.QueuePricesCommand.CanExecute(null)||model.QueueValuationCommand.CanExecute(null))throw new InvalidDataException("正式未配置来源许可时应保留11指数资料缺失并关闭采集："+model.Status);
                // 等图表过渡结束再留图，不能把插值中的位置当作真实公布值。
                timer.Restart();while(timer.Elapsed<TimeSpan.FromSeconds(2))Tick();
                var report=model.Report??throw new InvalidDataException("界面缺少后台报告身份。");
                if(model.HasChartValues||((FrameworkElement)window.Content).FindName("PriceChart") is not FrameworkElement chart||chart.IsVisible||((FrameworkElement)window.Content).FindName("MissingChart") is not FrameworkElement missing||!missing.IsVisible)throw new InvalidDataException("正式缺失资料应显示原因，不显示默认零刻度图表。");
                var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);using(var file=File.Create(screenshot)){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(file);}
                File.WriteAllText(Path.ChangeExtension(screenshot,".json"),JsonSerializer.Serialize(new{InstalledData=true,Transport="ActualWorkerBusinessPipe",RenderedAt=DateTimeOffset.UtcNow,model.Caption,model.Status,model.SourceStatus,Report=report},new JsonSerializerOptions{WriteIndented=true}));
                Console.WriteLine($"PASS 正式指数界面：{report.Rows.Count}指数，缺失与来源准入来自真实Worker；不伪造价格或估值。");
            }
            catch(Exception error){failure=error;}
            finally{window?.Close();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
    }
}
