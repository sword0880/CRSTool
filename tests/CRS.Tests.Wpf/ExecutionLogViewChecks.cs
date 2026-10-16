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
                    var plan=control.Plan;model.Jobs.Add(plan);model.Selected=plan;
                    var edit=new ScheduleEditViewModel(model,plan);
                    var scheduleDialog=new ScheduleEditWindow(edit){Owner=parent};scheduleDialog.Show();parent.UpdateLayout();
                    if(edit.Frequency!=CRS.PluginHost.CronFrequency.Daily || edit.Hour!=23 || edit.Minute!=15 || edit.NextTimes.Count!=5)throw new Exception("既有计划未还原或缺少时间预览。");
                    var picker=(ComboBox)scheduleDialog.FindName("FrequencyPicker");picker.SelectedValue=CRS.PluginHost.CronFrequency.Weekly;
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    edit.Weekdays.Single(x=>x.Code=="FRI").Selected=true;
                    if(edit.Cron!="0 15 23 ? * MON,FRI" || !edit.Valid)throw new Exception("可视化星期选项未生成表达式。");
                    foreach(var item in edit.Weekdays)item.Selected=false;
                    edit.TimeZoneId="UTC";
                    if(edit.Valid || edit.SaveCommand.CanExecute(null) || edit.NextTimes.Count!=0)throw new Exception("清空星期后仍允许保存旧表达式。");
                    edit.Weekdays[0].Selected=true;edit.Weekdays[4].Selected=true;edit.TimeZoneId="China Standard Time";
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);scheduleDialog.UpdateLayout();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    var generatorImage=new RenderTargetBitmap((int)scheduleDialog.ActualWidth,(int)scheduleDialog.ActualHeight,96,96,PixelFormats.Pbgra32);generatorImage.Render(scheduleDialog);
                    var generatorEncoder=new PngBitmapEncoder();generatorEncoder.Frames.Add(BitmapFrame.Create(generatorImage));using(var output=File.Create(Path.ChangeExtension(screenshot,"generator.png")))generatorEncoder.Save(output);
                    var advanced=new ScheduleEditViewModel(model,plan with{Cron="0 0 12 L * ?"});
                    if(!advanced.IsManual || advanced.Cron!="0 0 12 L * ?" || !advanced.Valid)throw new Exception("高级表达式被简化或丢失。");
                    edit.Frequency=CRS.PluginHost.CronFrequency.Manual;
                    edit.Cron=" ";await edit.SaveCommand.ExecuteAsync(null);
                    if(control.LastSchedule is not null || !edit.Status.Contains("请填写"))throw new Exception("空表达式发送到了后台。");
                    edit.Frequency=CRS.PluginHost.CronFrequency.Daily;edit.Hour=22;edit.Minute=20;
                    if(edit.Cron!="0 20 22 * * ?")throw new Exception("每日计划未生成正确表达式。");
                    model.Selected=plan with{JobId="other-job"};
                    control.ScheduleCode="Forbidden";await edit.SaveCommand.ExecuteAsync(null);
                    if(!scheduleDialog.IsVisible || !edit.Status.Contains("管理权限"))throw new Exception("无权限保存未保留编辑窗口。");
                    control.ScheduleCode="InvalidOrStaleRequest";await edit.SaveCommand.ExecuteAsync(null);
                    if(!scheduleDialog.IsVisible || !edit.Status.Contains("已被更改"))throw new Exception("冲突保存未保留输入。");
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var scheduleImage=new RenderTargetBitmap((int)scheduleDialog.ActualWidth,(int)scheduleDialog.ActualHeight,96,96,PixelFormats.Pbgra32);scheduleImage.Render(scheduleDialog);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))!);
                    var scheduleEncoder=new PngBitmapEncoder();scheduleEncoder.Frames.Add(BitmapFrame.Create(scheduleImage));using(var output=File.Create(Path.ChangeExtension(screenshot,"schedule.png")))scheduleEncoder.Save(output);
                    control.ScheduleCode="Ok";await edit.SaveCommand.ExecuteAsync(null);
                    if(scheduleDialog.IsVisible || control.LastSchedule is not {JobId:"official-share-sync",Cron:"0 20 22 * * ?",ExpectedRevision:7})throw new Exception("编辑窗口保存了其它选中的任务或错误修订号。");
                    var plans=(DataGrid)page.FindName("PlansGrid");plans.SelectedIndex=0;plans.ScrollIntoView(plan);parent.UpdateLayout();
                    var planRow=(DataGridRow)plans.ItemContainerGenerator.ContainerFromIndex(0);var opened=false;var lastRequest=control.LastSchedule;
                    _=parent.Dispatcher.BeginInvoke(new Action(()=>
                    {
                        var openedDialog=parent.OwnedWindows.OfType<ScheduleEditWindow>().Single();opened=true;
                        ((ScheduleEditViewModel)openedDialog.DataContext).Cron="0 1 1 * * ?";openedDialog.Close();
                    }));
                    plans.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=planRow});
                    if(!opened || control.LastSchedule!=lastRequest || model.Cron!=plan.Cron)throw new Exception("计划行双击或取消编辑行为错误。");
                    plans.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent,Source=plans});
                    if(parent.OwnedWindows.Count!=0)throw new Exception("计划空白双击打开了编辑窗口。");
                    var pageImage=new RenderTargetBitmap((int)parent.ActualWidth,(int)parent.ActualHeight,96,96,PixelFormats.Pbgra32);pageImage.Render(parent);
                    var pageEncoder=new PngBitmapEncoder();pageEncoder.Frames.Add(BitmapFrame.Create(pageImage));using(var output=File.Create(Path.ChangeExtension(screenshot,"plans.png")))pageEncoder.Save(output);
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
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);dialog.UpdateLayout();
                            var track=(System.Windows.Controls.Primitives.Track)scrollbar.Template.FindName("PART_Track",scrollbar);
                            if(track.Thumb.ActualHeight<40 || track.Thumb.TranslatePoint(new(0,track.Thumb.ActualHeight),track).Y>track.ActualHeight+1)throw new Exception("日志滑块过小或在末尾被裁切。");
                            var beforeDrag=viewport.VerticalOffset;
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0,0){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragStartedEvent});
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0,-60){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent});
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,-60,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            if(viewport.VerticalOffset>=beforeDrag)throw new Exception("滑块拖动事件没有驱动视口。");
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0,0){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragStartedEvent});
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0,-5000){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent});
                            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,-5000,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            dialog.UpdateLayout();if(viewport.VerticalOffset!=0)throw new Exception("拖动滚动条没有改变日志视口。");
                            control.Record=control.Record with{Progress=stages.Append(new ExecutionProgressEvent(92,control.Record.StartedUtc.AddSeconds(92),new("RequestTimedOut"))).ToArray()};
                            await log.RefreshCommand.ExecuteAsync(null);await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            if(viewport.VerticalOffset!=0)throw new Exception("刷新抢走了正在阅读的位置。");
                            log.OnlyIssues=true;
                            if(!log.LogText.Contains("请求超过20秒") || log.LogText.Contains("发起HTTPS请求"))throw new Exception("异常筛选未按阶段分类。");
                            log.Search="不存在的关键词";if(!log.LogText.Contains("没有符合"))throw new Exception("搜索零匹配未清空内容。");
                            if(!log.FullLogText.Contains("发起HTTPS请求"))throw new Exception("筛选破坏了复制用的完整日志。");
                            log.Search="";log.OnlyIssues=false;
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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
                    Console.WriteLine("PASS 计划编辑及日志：行双击/空白双击、取消、权限拒绝、修订冲突、固定任务保存；滑块拖动/最小尺寸、刷新保留位置、搜索/异常筛选、自动刷新和会话取消。");
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
        public TaskDefinition Plan {get;}=new("crs.national-team","official-share-sync","官方ETF份额同步","0 15 23 * * ?","China Standard Time",true,7,false);
        public string ScheduleCode {get;set;}="Ok";
        public BackgroundRequest? LastSchedule {get;private set;}
        public ExecutionRecord Record{get;set;}=new(Guid.NewGuid().ToString("N"),"crs.national-team","official-share-sync",new(2026,10,15,7,18,0,TimeSpan.Zero),new(2026,10,15,7,18,0,TimeSpan.Zero),null,"Running",null,1);
        public string Code{get;set;}="Ok";
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            if(request.Command=="ChangeSchedule"){LastSchedule=request;return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,ScheduleCode));}
            if(request.Command=="GetHealth")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Health:new("Running",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,Environment.ProcessId)));
            if(request.Command=="GetJobs")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Jobs:[Plan]));
            if(request.Command=="GetFilteredHistory")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",History:[Record]));
            if(request.Command!="GetExecution" || request.ExecutionId!=Record.ExecutionId)throw new InvalidOperationException("日志查询串到了其它执行或命令。");
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,Code,Execution:Code=="Ok"?Record:null));
        }
    }
}
