using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>真实WPF绑定和交互验收；使用隔离合成市场库，不连接邮箱或通知提供商。</summary>
internal static class ExitAlertUiChecks
{
    public static void Run()
    {
        Exception? failure=null;var thread=new Thread(()=>
        {
            var app=new System.Windows.Application();var root=Path.Combine(Environment.CurrentDirectory,"artifacts","exit-alert-ui",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
            using(var repository=TestMarketData.Open(Path.Combine(root,"data"),raw))
            {
                foreach(var fund in repository.Configuration().Where(x=>x.Ticker!="510300"))repository.SetMembership(new(fund.Market,fund.Ticker),fund.Name,fund.IndexGroup,false,"synthetic UI");
                repository.RegisterAlertDocument(new("SSE","510300","https://www.sse.com.cn/fixture-ui.pdf"),"%PDF-synthetic-ui"u8.ToArray());
                repository.EvaluateExitAlerts(true);
            }
            var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
            workspace.InitializeAsync(default).GetAwaiter().GetResult();using var lifetime=new CancellationTokenSource();
            var model=new ExitAlertViewModel(workspace,lifetime.Token);var page=new ExitAlertPage{DataContext=model};
            var window=new Window{Title="预警中心隔离合成验收",Content=new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},Width=1280,Height=960,ShowInTaskbar=false};
            window.Loaded+=async(_,_)=>{try
            {
                await model.RefreshCommand.ExecuteAsync(null);await model.ProofLoadTask;await Task.Delay(100);
                if(model.Events.Count!=1||!model.OverviewText.Contains("研究预览")||model.Routes.Single().ChannelId!="email")throw new Exception("预警门槛、事件或渠道引用未绑定。");
                model.SelectedEvent=model.Events.Single();await model.DetailLoadTask;
                if(!model.Detail.Contains("首次发现")||model.Changes.Count!=1)throw new Exception("事件详情或审计记录缺失。");
                if(model.MetadataConfirmed||model.HoldingConfirmed||model.IndependentOwnershipConfirmed||model.ProofDocuments.Count!=1)throw new Exception("原件登记自动升级人工确认。");
                Screenshot(window,Path.Combine(root,"events.png"));
                model.Search="绝不存在的合成搜索词";await model.QueryEventsCommand.ExecuteAsync(null);
                if(model.Events.Count!=0||model.SelectedEvent is not null||model.Changes.Count!=0||model.Detail.Length!=0)throw new Exception("空筛选残留旧事件或详情。");
                model.Search="";model.Lane="ETF份额异常";await model.QueryEventsCommand.ExecuteAsync(null);
                if(model.Events.Count!=0)throw new Exception("数据核查事件混入份额异常车道。");
                model.Lane="数据核查";await model.QueryEventsCommand.ExecuteAsync(null);
                if(model.Events.Count!=1)throw new Exception("车道筛选不正确。");
                await model.RefreshNotificationsCommand.ExecuteAsync(null);model.SelectedNotification=model.Notifications.Single();await model.DeliveryLoadTask;
                var tabs=Children(page).OfType<TabItem>().ToArray();tabs.Single(x=>Equals(x.Header,"通知记录")).IsSelected=true;
                await Task.Delay(100);Screenshot(window,Path.Combine(root,"notifications.png"));
                tabs.Single(x=>Equals(x.Header,"规则与订阅")).IsSelected=true;await Task.Delay(100);Screenshot(window,Path.Combine(root,"rules.png"));
                tabs.Single(x=>Equals(x.Header,"ETF原件与人工核验")).IsSelected=true;await Task.Delay(100);Screenshot(window,Path.Combine(root,"proof.png"));
                Console.WriteLine("PASS 预警WPF：研究门槛、三车道筛选、事件证据、空结果清理、独立订阅、原件不自动确认及通知状态；截图为合成数据："+root);
            }
            catch(Exception error){failure=error;}
            finally{lifetime.Cancel();await workspace.DisposeAsync();window.Close();}};
            app.Run(window);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("预警WPF验收失败。",failure);
    }
    private static void Screenshot(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}
    }
}
