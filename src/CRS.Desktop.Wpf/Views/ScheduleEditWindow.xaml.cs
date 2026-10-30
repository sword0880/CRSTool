// 计划编辑窗口的模型装配与生命周期绑定，保存或会话关闭时回收事件订阅和取消注册。
using System.Windows;
using CRS.DesktopClient.ViewModels;
using Wpf.Ui.Controls;
namespace CRS.DesktopClient.Views;
public partial class ScheduleEditWindow : FluentWindow
{
    public ScheduleEditWindow(ScheduleEditViewModel model)
    {
        InitializeComponent();DataContext=model;
        void CloseSaved(){if(IsLoaded)Close();}
        model.Saved+=CloseSaved;
        var registration=model.Lifetime.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(IsLoaded)Close();})));
        Closed+=(_,_)=>{registration.Dispose();model.Saved-=CloseSaved;};
        Loaded+=(_,_)=>{if(model.Lifetime.IsCancellationRequested)Close();else{CronEditor.Focus();CronEditor.SelectAll();}};
    }
    private void OnCancel(object sender,RoutedEventArgs e)=>Close();
}
