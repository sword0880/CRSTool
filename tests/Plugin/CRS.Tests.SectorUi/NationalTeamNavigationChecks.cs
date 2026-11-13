using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class NationalTeamNavigationChecks
{
    public static void Run()
    {
        Exception? failure=null;var passed=0;
        var thread=new Thread(()=>
        {
            void Check(bool condition,string name){if(!condition)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS 国家队分类导航："+name);}
            void Reject(Action action,string name)
            {
                try{action();}catch(InvalidDataException){Check(true,name);return;}
                throw new Exception("未拒绝："+name);
            }
            try
            {
                var firstCreates=0;var secondCreates=0;
                var sample=new NationalTeamWorkspaceItem[]{
                    new("fixture-etf","holdings","合成ETF","仅测试界面布局",()=>{firstCreates++;return new TextBlock{Text="合成ETF"};}),
                    new("fixture-pe","industry","合成行业PE","不构成低估证据",()=>{secondCreates++;return new TextBlock{Text="合成行业PE"};})
                };
                var page=new NationalTeamWorkspacePage(sample);
                page.Measure(new Size(1024,768));page.Arrange(new Rect(0,0,1024,768));
                Check(page.CategoryCount==2&&page.SelectedRouteKey is null&&firstCreates==0&&secondCreates==0,
                    "工作台先展示分类索引，不在插件启动时构造或加载任何业务页");
                page.OpenRoute("fixture-etf");
                Check(page.SelectedRouteKey=="fixture-etf"&&firstCreates==1&&secondCreates==0,"点击资金持仓才创建对应视图");
                page.OpenRoute("fixture-etf");
                Check(firstCreates==1,"同一目的地重复点击不重复构造");
                page.OpenRoute("fixture-pe");
                Check(page.SelectedRouteKey=="fixture-pe"&&secondCreates==1,"行业研究作为独立分类切换，不混合ETF身份");
                var body=Descendants(page).OfType<ContentControl>().Single(x=>x.Name=="ResearchContent");
                var header=Descendants(page).OfType<Border>().Single(x=>x.Name=="ResearchToolbar");
                foreach(var width in new[]{640,800,1280})
                {
                    page.Measure(new Size(width,760));page.Arrange(new Rect(0,0,width,760));page.UpdateLayout();
                    var origin=body.TranslatePoint(new Point(),page);
                    Check(Math.Abs(body.ActualWidth-width)<1&&Math.Abs(origin.X)<1&&origin.Y<85,
                        $"{width}DIP正文占满整行，顶部菜单不形成第二层侧栏");
                    Check(header.ActualWidth<=width-31&&!Descendants(page).OfType<Button>().Any(x=>x.Name=="ResearchNoticeToggle"),
                        $"{width}DIP移除集中研究声明按钮，菜单保留左右留白");
                }
                Check(!Descendants(page).OfType<Border>().Any(x=>x.Name=="ResearchNotice"),"不保留无功能的集中声明面板");
                Check(Descendants(page).OfType<PluginSectionNavigation>().Any(),"功能导航保留，证据提示继续由具体页面及菜单提供");
                var navigation=Descendants(page).OfType<PluginSectionNavigation>().Single();
                var shellSlot=new ContentControl();
                page.SetHeaderHosted(true);shellSlot.Content=page.HeaderContent;
                page.Measure(new Size(800,760));page.Arrange(new Rect(0,0,800,760));page.UpdateLayout();
                Check(body.TranslatePoint(new Point(),page).Y<1&&ReferenceEquals(shellSlot.Content,header),"移交宿主后菜单不重复占正文，使用同一菜单实例");
                page.OpenRoute("fixture-etf");
                Check(navigation.SelectedRouteKey=="fixture-etf","宿主顶栏仍同步当前路由");
                shellSlot.Content=null;page.SetHeaderHosted(false);page.SetHeaderHosted(false);
                Check(Descendants(page).OfType<Border>().Count(x=>x.Name=="ResearchToolbar")==1,"退出宿主后独立页恢复一次菜单，不重复挂载");
                var menuButton=Descendants(navigation).OfType<MenuItem>().Single(x=>AutomationProperties.GetName(x)=="资金与持仓菜单");
                var window=new Window{Content=page,Width=820,Height=760,ShowInTaskbar=false};
                try
                {
                    window.Show();Pump();
                    menuButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(menuButton)!,0,Key.F4)
                        {RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();
                    Check(menuButton.IsSubmenuOpen,"F4打开原生顶部菜单");
                    var destination=menuButton.Items.Cast<MenuItem>().Single();
                    destination.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(page.SelectedRouteKey=="fixture-etf"&&navigation.SelectedRouteKey=="fixture-etf"&&destination.IsChecked,
                        "菜单目的地打开对应页并同步选中态");
                    menuButton.IsSubmenuOpen=false;Pump();
                    Check(!menuButton.IsSubmenuOpen,"菜单关闭后不留下遮挡正文的弹层");
                }
                finally{window.Content=null;window.Close();}
                Reject(()=>page.OpenRoute("unknown"),"未知路由拒绝，不静默跳回默认页");
                Check(page.SelectedRouteKey=="fixture-etf","未知路由不破坏已经选中的页");
                Reject(()=>NationalTeamWorkspaceCatalog.Validate([sample[0],sample[0]]),"相同目的地不能出现两次");
                Reject(()=>NationalTeamWorkspaceCatalog.Validate([sample[0] with{CategoryKey="missing"}]),"未知分类不允许绕过导航登记");
                Reject(()=>NationalTeamWorkspaceCatalog.Validate([sample[0] with{EvidenceNotice=""}]),"研究页面没有证据边界说明不能登记");
                var broken=new NationalTeamWorkspacePage([sample[0] with{CreateView=()=>throw new IOException("synthetic view failure")},sample[1]]);
                broken.OpenRoute("fixture-pe");
                try{broken.OpenRoute("fixture-etf");throw new Exception("故障未上报");}
                catch(IOException){Check(broken.SelectedRouteKey=="fixture-pe","功能视图构造失败不会清空已打开内容");}

                var session=new NationalTeamPluginEntry().CreateSession(null!);
                try
                {
                    session.StartAsync(default).GetAwaiter().GetResult();
                    var registered=((IPluginWpfSession)session).GetPages();
                    Check(registered.Count==1&&registered.Select(x=>x.RouteKey).Distinct(StringComparer.Ordinal).Count()==registered.Count
                        &&registered.Count(x=>x.ShowInNavigation)==1&&registered.Single(x=>x.ShowInNavigation).RouteKey=="national-team-workspace",
                        "NationalTeamWatch主导航只有一个工作台入口");
                    Check(!registered.Any(x=>x.RouteKey is "etf-monitor" or "direct-equity" or "industry-research"),
                        "删除三个历史宿主路由，业务只在工作台内部导航");
                    var root=registered.Single(x=>x.ShowInNavigation).CreateView();
                    Check(root is NationalTeamWorkspacePage {CategoryCount:2,SelectedRouteKey:null},
                        "真正插件入口仅构造分类工作台，后台操作未在Startup触发");
                }
                finally {session.StopAsync(default).GetAwaiter().GetResult();session.DisposeAsync().AsTask().GetAwaiter().GetResult();}
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("国家队分类导航验收失败。",failure);
        Console.WriteLine($"PASS 国家队分类导航 {passed} 项；仅界面与目录，未连接后台正式数据。");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var descendant in Descendants(child))yield return descendant;
        }
    }
    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
}
