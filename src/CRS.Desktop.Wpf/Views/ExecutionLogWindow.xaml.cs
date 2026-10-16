using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

/// <summary>查看公开执行快照；关闭、锁定或父会话取消后停止刷新并释放窗口。</summary>
public partial class ExecutionLogWindow : FluentWindow
{
    private bool following=true;
    private bool restoring;
    private int navigationVersion;
    private System.Windows.Controls.ScrollViewer? Viewport=>LogEditor.Template.FindName("PART_ContentHost",LogEditor) as System.Windows.Controls.ScrollViewer;
    public ExecutionLogWindow(ExecutionLogViewModel model)
    {
        InitializeComponent();DataContext=model;
        model.PropertyChanging+=OnModelChanging;
        // 自动刷新只读当前执行，关闭或会话取消后停止，不触发任何新采集。
        var refresh=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        refresh.Tick+=async(_,_)=>{if(!model.IsRunning){refresh.Stop();return;}if(!model.RefreshCommand.IsRunning)await model.RefreshCommand.ExecuteAsync(null);};
        var registration=model.Lifetime.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(IsLoaded)Close();})));
        Loaded+=async(_,_)=>{await model.RefreshCommand.ExecuteAsync(null);if(IsLoaded && !model.Lifetime.IsCancellationRequested && model.IsRunning)refresh.Start();};
        Closed+=(_,_)=>{refresh.Stop();registration.Dispose();model.PropertyChanging-=OnModelChanging;model.Dispose();};
    }
    private void OnModelChanging(object? sender,System.ComponentModel.PropertyChangingEventArgs e)
    {
        if(e.PropertyName!=nameof(ExecutionLogViewModel.LogText) || Viewport is not {} viewport)return;
        var offset=viewport.VerticalOffset;var selection=LogEditor.SelectionStart;var length=LogEditor.SelectionLength;var version=navigationVersion;
        restoring=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>
        {
            if(!IsLoaded || version!=navigationVersion){restoring=false;return;}
            if(following)LogEditor.ScrollToEnd();
            else{LogEditor.Select(Math.Min(selection,LogEditor.Text.Length),Math.Min(length,Math.Max(0,LogEditor.Text.Length-selection)));viewport.ScrollToVerticalOffset(offset);LatestButton.Content="有新内容 · 回到最新";}
            restoring=false;UpdateFollowLabel();
        }));
    }
    private void UpdateFollowLabel()=>FollowLabel.Text=following?"正在跟随最新日志":"已暂停跟随 · 保持阅读位置";
    private void OnViewportChanged(object sender,System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if(restoring || e.ExtentHeightChange!=0 || e.VerticalChange==0)return;
        if(sender is System.Windows.Controls.ScrollViewer viewport)following=viewport.ScrollableHeight-viewport.VerticalOffset<2;
        if(following)LatestButton.Content="回到最新";
        UpdateFollowLabel();
    }
    private void OnLogWheel(object sender,System.Windows.Input.MouseWheelEventArgs e)
    {
        if(Viewport is not {} viewport)return;
        navigationVersion++;
        following=false;
        var lines=System.Windows.SystemParameters.WheelScrollLines;
        viewport.ScrollToVerticalOffset(viewport.VerticalOffset-e.Delta/120d*(lines<0?viewport.ViewportHeight:Math.Max(1,lines)*22));
        e.Handled=true;UpdateFollowLabel();
    }
    private void OnLogKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(Viewport is not {} viewport)return;
        switch(e.Key)
        {
            case System.Windows.Input.Key.PageUp:following=false;viewport.PageUp();break;
            case System.Windows.Input.Key.PageDown:viewport.PageDown();break;
            case System.Windows.Input.Key.Home when System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control):OnScrollStart(sender,e);break;
            case System.Windows.Input.Key.End when System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control):OnScrollEnd(sender,e);break;
            default:return;
        }
        navigationVersion++;e.Handled=true;UpdateFollowLabel();
    }
    private void OnCopy(object sender,RoutedEventArgs e)
    {
        try{Clipboard.SetText(((ExecutionLogViewModel)DataContext).FullLogText);FollowLabel.Text="已复制本次执行的全部日志";}
        catch(System.Runtime.InteropServices.COMException){FollowLabel.Text="剪贴板忙，请稍后重试";}
    }
    private void OnClose(object sender,RoutedEventArgs e)=>Close();
    // 仅改变日志视口，不请求新任务或读取其它执行。
    private void OnScrollStart(object sender,RoutedEventArgs e){navigationVersion++;following=false;LogEditor.ScrollToHome();UpdateFollowLabel();}
    private void OnScrollEnd(object sender,RoutedEventArgs e){navigationVersion++;following=true;LogEditor.ScrollToEnd();LatestButton.Content="回到最新";UpdateFollowLabel();}
    /// <summary>滚动由 ScrollViewer 原生命令驱动；Scroll 事件里的值可能尚未更新，不能再次写回旧值。</summary>
    private void OnLogScroll(object sender,System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if(e.ScrollEventType==System.Windows.Controls.Primitives.ScrollEventType.EndScroll)return;
        navigationVersion++;following=false;restoring=false;UpdateFollowLabel();
    }
}
