using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using System.Windows;
using System.Windows.Controls;

/// <summary>真实 WPF 页面验证最近执行深链接与异常筛选控件，不连接或修改正式 Worker。</summary>
internal static class TaskCenterDeepLinkChecks
{
    public static void Run()
    {
        Exception? error=null;
        var thread=new Thread(() =>
        {
            try
            {
                var app=new Application();
                var model=new BackgroundServiceViewModel(CancellationToken.None);
                var page=new BackgroundServicePage(model);
                var window=new Window{Content=page,Width=1200,Height=780,ShowActivated=false,ShowInTaskbar=false};
                var tabs=page.FindName("TaskCenterTabs") as TabControl
                    ?? throw new InvalidOperationException("任务中心缺少明确命名的TabControl。");
                // 尚未Loaded的TabControl允许 SelectedIndex=-1，呈现后才会自动选中首个页签。
                if(tabs.SelectedIndex is not (-1 or 0))throw new InvalidOperationException("任务中心初始页签异常。");
                page.ShowRecentExecutions();
                if(tabs.SelectedIndex!=1 || (tabs.SelectedItem as TabItem)?.Header?.ToString()!="最近执行")
                    throw new InvalidOperationException("右下角直达未选中最近执行页签。");
                var check=page.FindName("OnlyIssuesCheckBox") as CheckBox
                    ?? throw new InvalidOperationException("最近执行缺少异常筛选控件。");
                model.OnlyIssues=true;
                page.Measure(new(1200,780));page.Arrange(new(0,0,1200,780));page.UpdateLayout();
                if(check.IsChecked!=true)throw new InvalidOperationException("异常筛选未绑定到任务模型。");
                model.OnlyIssues=false;
                window.UpdateLayout();
                if(check.IsChecked!=false)throw new InvalidOperationException("恢复全部记录未清除勾选。");
                foreach(var status in new[]{"RetryDeferred","Failed","AccessBlocked","SourceSchemaChanged","Interrupted","Rejected"})
                    if(!BackgroundServiceViewModel.IsIssueResult(status))
                        throw new InvalidOperationException("异常状态丢失："+status);
                foreach(var status in new[]{"Succeeded","NotPublished","Running","Cancelled"})
                    if(BackgroundServiceViewModel.IsIssueResult(status))
                        throw new InvalidOperationException("普通/主动取消状态误报为异常："+status);
                Console.WriteLine("PASS 任务中心底栏深链接：最近执行页签、异常勾选绑定、正常/异常结果分类。");
            }
            catch(Exception failure){error=failure;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();
        if(error is not null)throw error;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T match)yield return match;
            foreach(var descendant in Find<T>(child))yield return descendant;
        }
    }
}
