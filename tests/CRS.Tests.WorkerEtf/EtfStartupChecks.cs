using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Plugins.NationalTeamWatch.Domain;

internal static class EtfStartupChecks
{
    internal static void Run()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();
            app.Resources["BooleanVisibility"]=new System.Windows.Controls.BooleanToVisibilityConverter();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Dispatcher.InvokeAsync(async()=>
            {
                try
                {
                    await using var session=new NationalTeamPluginEntry().CreateSession(null!);
                    var clock=Stopwatch.StartNew();await session.StartAsync(default);
                    Check(clock.Elapsed<TimeSpan.FromSeconds(2),"会话启动不等待后台连接或整页刷新");
                    Check(((IPluginWpfSession)session).GetPages().Count==2,"先完成页面注册");
                    using var lifetime=new CancellationTokenSource();
                    var workspace=DispatchProxy.Create<IEtfWorkspace,DelayedWorkspace>();
                    var delayed=(DelayedWorkspace)(object)workspace;
                    var model=new EtfViewModel(workspace,lifetime.Token);
                    var page=new EtfPage(model);
                    page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    var refresh=model.EnsureInitializedAsync();
                    Check(!refresh.IsCompleted&&model.Busy&&delayed.Reads==1,"页面打开后独立开始刷新");
                    Check(model.Configurations.Count==24,"份额概览尚在等待时24只ETF清单已可见");
                    Check(ReferenceEquals(refresh,model.EnsureInitializedAsync()),"重复导航共享首次刷新任务");
                    await Task.Delay(TimeSpan.FromSeconds(16));
                    Check(!refresh.IsCompleted&&!delayed.Token.IsCancellationRequested,"慢查询超过宿主15秒启动期限仍未被取消");
                    delayed.Pending.SetException(new InvalidDataException("隔离测试：后台暂不可用"));
                    await refresh;
                    Check(!model.Busy&&model.Status.Contains("后台暂不可用"),"数据错误显示在页面且释放忙碌状态");
                    Check(model.Configurations.Count==24,"概览读取失败不清空ETF清单");
                    await model.RefreshCommand.ExecuteAsync(null);
                    Check(delayed.Reads==2,"首次失败后允许手动刷新");
                    lifetime.Cancel();
                    await session.StopAsync(default);
                }
                catch(Exception error){failure=error;}
                finally{app.Shutdown();}
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("ETF启动回归失败",failure);
    }
    private static void Check(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);Console.WriteLine("PASS ETF启动："+message);}
    public class DelayedWorkspace:DispatchProxy
    {
        public int Reads;
        public CancellationToken Token;
        public TaskCompletionSource<List<EtfSummary>> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(method?.Name=="get_CanManage")return false;
            if(method?.Name=="ConfigurationAsync")return Task.FromResult(InitialEtfDirectory.Entries.Select(x=>new EtfConfiguration(x.Market,x.Ticker,x.Name,x.Index,true,"初始化",InitialEtfDirectory.Origin,0)).ToList());
            if(method?.Name=="OverviewAsync")
            {Reads++;Token=(CancellationToken)args![0]!;return Pending.Task.WaitAsync(Token);}
            throw new NotSupportedException(method?.Name);
        }
    }
}
