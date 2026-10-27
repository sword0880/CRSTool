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
                repository.ReviewNamedEtfHolding(new(repository.AlertDocuments(new("SSE","510300")).Single().Id,"synthetic-ui-legal","合成具名法人甲","合成独立账户甲",new(2026,6,30),
                    new(2026,8,28,18,0,0,TimeSpan.FromHours(8)),12345,"合成第10页","synthetic-ui-reviewer",true,true));
                repository.EvaluateExitAlerts(true);
            }
            var workspace=CRS.Plugins.NationalTeamWatch.Infrastructure.NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
            workspace.InitializeAsync(default).GetAwaiter().GetResult();using var lifetime=new CancellationTokenSource();
            var model=new ExitAlertViewModel(new LocalResearchWorkspace(workspace),lifetime.Token);var page=new ExitAlertPage{DataContext=model};
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
                var proofTab=tabs.Single(x=>Equals(x.Header,"ETF原件与人工核验"));var scroll=(ScrollViewer)proofTab.Content;
                scroll.ScrollToEnd();await Task.Delay(100);window.UpdateLayout();
                var proofGrid=Children(page).OfType<DataGrid>().Single(x=>ReferenceEquals(x.ItemsSource,model.ProofHoldings));
                var column=proofGrid.Columns.OfType<DataGridCheckBoxColumn>().Single(x=>(x.Binding as System.Windows.Data.Binding)?.Path.Path==nameof(NamedEtfHolding.OriginalConfirmed));
                if(model.ProofHoldings.Count!=1||column.GetCellContent(model.ProofHoldings.Single()) is not CheckBox {IsChecked:true})throw new Exception("真实原件核验状态未绑定。");
                ScreenshotElement(proofGrid,Path.Combine(root,"holdings-verified.png"));
                using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"data","market.db"),Pooling=false}.ToString()))
                {
                    db.Open();using var command=db.CreateCommand();
                    command.CommandText="INSERT INTO quality_issue SELECT 'ui-proof-q15','Q15','fixture',fetch_id,'Critical','Open',fetch_id,NULL,NULL FROM nt_alert_document WHERE ticker='510300' LIMIT 1";command.ExecuteNonQuery();
                }
                await model.RefreshCommand.ExecuteAsync(null);await model.ProofLoadTask;scroll.ScrollToEnd();await Task.Delay(100);window.UpdateLayout();
                if(model.ProofHoldings.Single().OriginalConfirmed||column.GetCellContent(model.ProofHoldings.Single()) is not CheckBox {IsChecked:false})throw new Exception("相同基金刷新后仍显示旧原件确认。");
                Screenshot(window,Path.Combine(root,"proof-isolated.png"));
                ScreenshotElement(proofGrid,Path.Combine(root,"holdings-isolated.png"));
                Console.WriteLine("PASS 预警WPF：相同基金刷新重新读取证据状态，真实核验列由通过变为隔离，不残留旧确认。");
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
    private static void ScreenshotElement(FrameworkElement element,string path)
    {
        // 滚动容器会裁剪屏外表格；临时独立承载同一控件和数据，捕获后恢复原位置。
        var parent=(Panel)element.Parent;var index=parent.Children.IndexOf(element);var context=element.DataContext;
        var local=element.ReadLocalValue(FrameworkElement.DataContextProperty);var width=element.ActualWidth;var height=element.ActualHeight;
        parent.Children.Remove(element);element.DataContext=context;
        var window=new Window{Content=element,Width=width,Height=height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(element);
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            if(pixels.Distinct().Count()<8)throw new InvalidOperationException("表格截图为空，不能用作界面证据。");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
        }
        finally{window.Content=null;window.Close();parent.Children.Insert(index,element);if(local==DependencyProperty.UnsetValue)element.ClearValue(FrameworkElement.DataContextProperty);else element.SetValue(FrameworkElement.DataContextProperty,local);}
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}
    }
}
