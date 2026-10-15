using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.DesktopClient.Views.Pages;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;

/// <summary>固定服务响应配合真实双击与模态窗口，验证日志不会串到其它执行记录。</summary>
internal static class ExecutionLogViewChecks
{
    public static void Run(string screenshot)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var application=new System.Windows.Application();
            application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            using var lifetime=new CancellationTokenSource();using var stop=new CancellationTokenSource();
            var control=new FixtureControl();var pipe="CRS.ExecutionLog.Tests."+Guid.NewGuid().ToString("N");
            var server=new BackgroundPipeServer(control,pipe).RunAsync(stop.Token);
            var client=new BackgroundPipeClient(pipe);
            var model=new BackgroundServiceViewModel(lifetime.Token,client);model.History.Add(control.Record);
            var page=new BackgroundServicePage(model);
            var parent=new Window{Content=page,Width=1200,Height=800};
            parent.Loaded+=async(_,_)=>
            {
                try
                {
                    var tabs=Find<TabControl>(page)!;tabs.SelectedIndex=1;parent.UpdateLayout();
                    var grid=(DataGrid)page.FindName("HistoryGrid");grid.SelectedIndex=0;grid.ScrollIntoView(control.Record);parent.UpdateLayout();
                    var row=(DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                    async Task VerifyDialog()
                    {
                        ExecutionLogWindow? dialog=null;
                        try
                        {
                            await Task.Delay(250);dialog=parent.OwnedWindows.OfType<ExecutionLogWindow>().Single();
                            var log=(ExecutionLogViewModel)dialog.DataContext;
                            while(log.RefreshCommand.IsRunning)await Task.Delay(20);
                            if(log.ExecutionId!=control.Record.ExecutionId || !log.LogText.Contains("进行中") || !log.LogText.Contains("尚未保存结束摘要"))throw new Exception("双击没有打开选中执行的日志。");
                            // 等待真实窗口的3秒自动刷新，不能以手动刷新冒充进行中的进度可见性。
                            control.Record=control.Record with{Progress=[new(1,control.Record.StartedUtc.AddSeconds(1),new("RateWait",new(2026,10,14),"SZSE",Until:control.Record.StartedUtc.AddSeconds(4)))]};
                            var deadline=DateTimeOffset.UtcNow.AddSeconds(7);
                            while(!log.LogText.Contains("按来源间隔等待")){if(DateTimeOffset.UtcNow>deadline)throw new Exception("运行阶段未自动刷新。");await Task.Delay(50);}
                            if(!log.ProgressLabel.Contains("最近阶段") || !log.LogText.Contains("最早继续时间"))throw new Exception("缺少等待截止或最近进展时间。");
                            control.Record=control.Record with{EndedUtc=control.Record.StartedUtc.AddSeconds(45),Result="Succeeded",Checkpoint="days=8;sourceIssues=0;rows=25",Progress=[new(2,control.Record.StartedUtc.AddSeconds(45),new("RangeCompleted",Received:25,Added:1,Duplicates:23,Pending:1))]};
                            await log.RefreshCommand.ExecuteAsync(null);
                            if(!log.LogText.Contains("收到 25 条记录") || !log.LogText.Contains("2026-10-15 15:18:00") || !log.LogText.Contains("成功"))throw new Exception("刷新未展示同一记录的实际结果、摘要和北京时间。");
                            if(!log.LogText.Contains("新增已接受1条，重复跳过23条，新增待审核1条"))throw new Exception("日志把收到数量当作新增数量。");
                            // 长日志验证真实可滚动区域及可见滑块，避免只有一根装饰性的边条。
                            var stages=Enumerable.Range(1,90).Select(index=>new ExecutionProgressEvent(index,control.Record.StartedUtc.AddSeconds(index),new("RequestStarted",new(2026,10,14),"SSE"))).ToList();
                            stages.Add(new(91,control.Record.StartedUtc.AddSeconds(91),new("RangeCompleted",Received:25,Added:1,Duplicates:23,Pending:1)));
                            control.Record=control.Record with{Progress=stages,EndedUtc=control.Record.StartedUtc.AddSeconds(91)};await log.RefreshCommand.ExecuteAsync(null);dialog.UpdateLayout();
                            var editor=(TextBox)dialog.FindName("LogEditor");editor.ApplyTemplate();
                            var viewport=(ScrollViewer)editor.Template.FindName("PART_ContentHost",editor);viewport.ApplyTemplate();
                            var scrollbar=(System.Windows.Controls.Primitives.ScrollBar)viewport.Template.FindName("PART_VerticalScrollBar",viewport);
                            if(!scrollbar.IsVisible || scrollbar.ActualWidth<12 || viewport.ScrollableHeight<=0)throw new Exception("长日志缺少可见且可用的滚动条。");
                            editor.ScrollToEnd();dialog.UpdateLayout();if(viewport.VerticalOffset<=0)throw new Exception("日志无法滚动到末尾。");
                            scrollbar.RaiseEvent(new System.Windows.Controls.Primitives.ScrollEventArgs(System.Windows.Controls.Primitives.ScrollEventType.ThumbPosition,0){RoutedEvent=System.Windows.Controls.Primitives.ScrollBar.ScrollEvent});
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            dialog.UpdateLayout();if(viewport.VerticalOffset!=0)throw new Exception("拖动滚动条没有改变日志视口。");
                            editor.ScrollToEnd();dialog.UpdateLayout();
                            dialog.UpdateLayout();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                            var image=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(dialog);
                            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(screenshot))encoder.Save(output);
                            dialog.Width=720;dialog.Height=500;dialog.UpdateLayout();
                            if(editor.ActualHeight<50 || ((FrameworkElement)dialog.FindName("CloseButton")).TranslatePoint(new(0,38),dialog).Y>dialog.ActualHeight)throw new Exception("最小日志窗口挤掉了内容或关闭按钮。");
                            control.Code="UnknownExecution";await log.RefreshCommand.ExecuteAsync(null);
                            if(!log.Status.Contains("清理") || !log.LogText.Contains("收到 25 条记录"))throw new Exception("清理后的记录未保留带提示的快照。");
                            control.Code="UnknownCommand";await log.RefreshCommand.ExecuteAsync(null);
                            if(!log.Status.Contains("更新服务"))throw new Exception("旧服务未提示更新。");
                            dialog.Close();
                        }
                        catch(Exception error){failure=error;foreach(var owned in parent.OwnedWindows.OfType<ExecutionLogWindow>().ToArray())owned.Close();}
                    }
                    Task? verification=null;
                    _ = parent.Dispatcher.BeginInvoke(new Action(()=>verification=VerifyDialog()));
                    // WPF双击是直接路由事件，交给DataGrid并保留实际数据行作为原始来源。
                    grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=row});
                    if(verification is null)throw new Exception("行双击没有进入日志模态窗口。");
                    await verification;
                    if(failure is not null)throw failure;
                    grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=grid});
                    if(parent.OwnedWindows.Count!=0)throw new Exception("双击空白处错误打开了日志。");
                    // 空白检查在会话仍有效时进行，随后独立验证父会话取消会关闭窗口。
                    var cancelledDialog=new ExecutionLogWindow(model.CreateExecutionLog(control.Record)){Owner=parent};cancelledDialog.Show();
                    await Task.Delay(100);lifetime.Cancel();await Task.Delay(100);
                    if(parent.OwnedWindows.OfType<ExecutionLogWindow>().Any())throw new Exception("会话取消后日志窗口未关闭。");
                    Console.WriteLine("PASS 执行日志：真实行双击、刷新终态、固定执行ID、采集摘要、北京时间、清理快照、旧服务提示、会话取消及空白双击。");
                }
                catch(Exception error){failure=error;}
                finally {stop.Cancel();parent.Close();}
            };
            application.Run(parent);
            try{server.GetAwaiter().GetResult();}catch(OperationCanceledException){ }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("执行日志窗口验收失败。",failure);
    }
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)return match;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is { } child)return child;
        return null;
    }
    private sealed class FixtureControl:IBackgroundControl
    {
        public ExecutionRecord Record{get;set;}=new(Guid.NewGuid().ToString("N"),"crs.national-team","official-share-sync",new(2026,10,15,7,18,0,TimeSpan.Zero),new(2026,10,15,7,18,0,TimeSpan.Zero),null,"Running",null,1);
        public string Code{get;set;}="Ok";
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command!="GetExecution" || request.ExecutionId!=Record.ExecutionId)throw new InvalidOperationException("日志查询串到了其它执行或命令。");
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,Code,Execution:Code=="Ok"?Record:null));
        }
    }
}
