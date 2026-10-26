using System.IO;
using System.Windows;
using System.Windows.Controls;
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
                Reject(()=>page.OpenRoute("unknown"),"未知路由拒绝，不静默跳回默认页");
                Check(page.SelectedRouteKey=="fixture-pe","未知路由不破坏已经选中的页");
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
                    Check(registered.Select(x=>x.RouteKey).Distinct(StringComparer.Ordinal).Count()==registered.Count
                        &&registered.Count(x=>x.ShowInNavigation)==1&&registered.Single(x=>x.ShowInNavigation).RouteKey=="national-team-workspace",
                        "NationalTeamWatch主导航只有一个工作台入口");
                    Check(new[]{"etf-monitor","direct-equity","industry-research"}.All(route=>
                        registered.Any(x=>x.RouteKey==route&&!x.ShowInNavigation)),"三个历史路由仍存在但不重复堆满宿主侧栏");
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
}
