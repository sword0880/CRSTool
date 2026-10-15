using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;

namespace CRS.DesktopClient.Views;

/// <summary>查看公开执行快照；关闭、锁定或父会话取消后停止刷新并释放窗口。</summary>
public partial class ExecutionLogWindow : FluentWindow
{
    public ExecutionLogWindow(ExecutionLogViewModel model)
    {
        InitializeComponent();DataContext=model;
        // 自动刷新只读当前执行，关闭或会话取消后停止，不触发任何新采集。
        var refresh=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};
        refresh.Tick+=async(_,_)=>{if(!model.IsRunning){refresh.Stop();return;}if(!model.RefreshCommand.IsRunning)await model.RefreshCommand.ExecuteAsync(null);};
        var registration=model.Lifetime.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(IsLoaded)Close();})));
        Loaded+=async(_,_)=>{await model.RefreshCommand.ExecuteAsync(null);if(IsLoaded && !model.Lifetime.IsCancellationRequested && model.IsRunning)refresh.Start();};
        Closed+=(_,_)=>{refresh.Stop();registration.Dispose();model.Dispose();};
    }
    private void OnClose(object sender,RoutedEventArgs e)=>Close();
    // 仅改变日志视口，不请求新任务或读取其它执行。
    private void OnScrollStart(object sender,RoutedEventArgs e)=>LogEditor.ScrollToHome();
    private void OnScrollEnd(object sender,RoutedEventArgs e)=>LogEditor.ScrollToEnd();
    /// <summary>定制模板显式转发拖动和翻页事件，避免滚动条可见却没有驱动文本视口。</summary>
    private void OnLogScroll(object sender,System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if(e.ScrollEventType==System.Windows.Controls.Primitives.ScrollEventType.EndScroll)return;
        for(DependencyObject? parent=sender as DependencyObject;parent is not null;parent=System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if(parent is System.Windows.Controls.ScrollViewer viewer){viewer.ScrollToVerticalOffset(e.NewValue);e.Handled=true;return;}
    }
}
