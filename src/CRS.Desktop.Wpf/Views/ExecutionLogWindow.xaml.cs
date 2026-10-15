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
        var registration=model.Lifetime.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(IsLoaded)Close();})));
        Loaded+=async(_,_)=>await model.RefreshCommand.ExecuteAsync(null);
        Closed+=(_,_)=>{registration.Dispose();model.Dispose();};
    }
    private void OnClose(object sender,RoutedEventArgs e)=>Close();
}
