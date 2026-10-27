using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.Plugin.Wpf;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using TextBlock = System.Windows.Controls.TextBlock;

/// <summary>真实 WPF 控件交互；分离主窗口 Loaded，避免启动插件、正式服务或个人保险库。</summary>
internal static class CompactNavigationChecks
{
    internal static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach(var name in new[]{"BrandPalette","SharedStyles"})
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri($"pack://application:,,,/CRS.Desktop.Wpf;component/Styles/{name}.xaml")});
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(((SolidColorBrush)app.FindResource("BrandAccentBrush")).Color,Wpf.Ui.Appearance.ApplicationTheme.Light);
            var shell=new MainWindow(new ShellViewModel(new FakeUseCases(),new SilentInteraction()));
            var layout=(FrameworkElement)shell.Content;shell.Content=null;layout.DataContext=shell.DataContext;
            var canvas=new Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=layout};
            var host=new Window{Content=canvas,Width=1040,Height=700,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            Directory.CreateDirectory(directory);
            host.Loaded+=async(_,_)=>
            {
                try
                {
                    var investment=(NavigationViewItem)shell.FindName("InvestmentGroup");
                    var tax=(NavigationViewItem)shell.FindName("TaxGroup");
                    var extensions=(NavigationViewItem)shell.FindName("ExtensionsGroup");
                    var toggle=(System.Windows.Controls.Button)shell.FindName("NavigationToggleButton");
                    var pluginSurface=(FrameworkElement)shell.FindName("PluginSurface");
                    var navigation=(NavigationView)shell.FindName("Navigation");
                    Check(shell.Navigate("trades"),"原有内置路由可打开");await Task.Delay(200);
                    Check(!shell.IsNavigationExpanded&&investment.IsActive&&investment.ToolTip?.ToString()=="投资与数据","折叠分类高亮与悬停名称");
                    var initialWidth=layout.ActualWidth;var initialOffset=pluginSurface.Margin.Left;
                    Invoke(tax);await Task.Delay(120);
                    var menu=Menu(shell);
                    Check(menu is {IsOpen:true}&&menu.Tag?.ToString()=="税务与报表"&&menu.Items.Count==5,"分类浮层显示标题和完整子菜单");
                    Check(!shell.IsNavigationExpanded&&layout.ActualWidth==initialWidth&&pluginSurface.Margin.Left==initialOffset,"浮层不展开侧栏或挤动页面");
                    Capture(menu!,Path.Combine(directory,"tax-flyout.png"));
                    Invoke(menu!.Items.OfType<MenuItem>().Single(x=>Equals(x.Header,"汇率管理")));await Task.Delay(120);
                    Check(Menu(shell) is null&&!shell.IsNavigationExpanded&&tax.IsActive&&!investment.IsActive,
                        "选择子项后关闭浮层并只高亮当前分类");
                    Check(navigation.SelectedItem is NavigationViewItem {TargetPageType:var type}&&type==MainWindow.PageTypes["rates"],"浮层进入原有页面");
                    Invoke(tax);await Task.Delay(80);menu=Menu(shell)!;
                    Check(menu.Items.OfType<MenuItem>().Single(x=>Equals(x.Header,"汇率管理")).IsChecked,"重新打开浮层标识当前页面");
                    menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(host)!,Environment.TickCount,Key.Escape)
                        {RoutedEvent=Keyboard.PreviewKeyDownEvent});
                    Check(Menu(shell) is null&&tax.IsKeyboardFocused,"Esc关闭并恢复分类焦点");
                    Invoke(tax);await Task.Delay(80);var oldMenu=Menu(shell)!;
                    Invoke(investment);await Task.Delay(80);
                    Check(!oldMenu.IsOpen&&Menu(shell)?.Tag?.ToString()=="投资与数据"&&tax.IsActive,"切换分类只保留一个浮层且不改变当前页面");
                    Invoke(toggle);await Task.Delay(240);
                    Check(shell.IsNavigationExpanded&&Menu(shell) is null&&tax.IsExpanded,"手动展开关闭浮层并展开当前分类");
                    Invoke(investment);await Task.Delay(100);
                    Check(investment.IsExpanded&&!tax.IsExpanded&&Menu(shell) is null,"展开态继续使用互斥的内联菜单树");
                    Invoke(toggle);await Task.Delay(240);
                    var down=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent};
                    investment.RaiseEvent(down);
                    var up=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                        {RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent};
                    investment.RaiseEvent(up);await Task.Delay(80);
                    Check(down.Handled&&up.Handled&&Menu(shell)?.Tag?.ToString()=="投资与数据","鼠标输入由分类浮层接管");
                    Invoke(toggle);await Task.Delay(220);Invoke(toggle);await Task.Delay(220);
                    investment.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(host)!,Environment.TickCount,Key.Right)
                        {RoutedEvent=Keyboard.PreviewKeyDownEvent});await Task.Delay(80);
                    Check(Menu(shell)?.Tag?.ToString()=="投资与数据","右方向键可打开分类浮层");
                    // 模拟按插件契约动态添加的入口，原 Click 处理器仍接收同一菜单对象。
                    var dynamicItem=new NavigationViewItem{Content="合成插件页面",Icon=new SymbolIcon{Symbol=SymbolRegular.Apps24}};
                    var opened=0;
                    dynamicItem.Click+=(_,_)=>
                    {
                        opened++;
                        Method("ShowHostedPage").Invoke(shell,[new Border{Child=new TextBlock{Text="合成插件页面"}}]);
                    };
                    Method("RegisterNavigationLeaf").Invoke(shell,[dynamicItem]);extensions.MenuItems.Add(dynamicItem);
                    Invoke(extensions);await Task.Delay(80);
                    Check(Menu(shell)?.Items.Count==1,"动态扩展自动出现在分类浮层");
                    Invoke(Menu(shell)!.Items.OfType<MenuItem>().Single());await Task.Delay(80);
                    Check(opened==1&&extensions.IsActive&&!tax.IsActive&&Menu(shell) is null&&pluginSurface.Visibility==Visibility.Visible,
                        "动态菜单沿用原处理器并同步高亮");
                    Check(shell.Navigate("history"),"插件页可返回内置页面");await Task.Delay(100);
                    Check(pluginSurface.Visibility==Visibility.Collapsed&&!extensions.IsActive&&!shell.IsNavigationExpanded,"返回内置页面清除插件选中且保持折叠");
                    var headerSlot=(ContentControl)shell.FindName("PluginHeaderContent");var fixture=new HeaderFixture();
                    Method("ShowHostedPage").Invoke(shell,[fixture]);await Task.Delay(80);host.UpdateLayout();
                    Check(ReferenceEquals(headerSlot.Content,fixture.HeaderContent)&&fixture.Hosted,"插件菜单移交实际窗口顶栏");
                    foreach(var width in new[]{1040,1280})
                    {
                        host.Width=width;await Task.Delay(80);host.UpdateLayout();
                        var point=fixture.HeaderContent.TranslatePoint(new Point(),layout);
                        Check(point.Y>=0&&point.Y+fixture.HeaderContent.ActualHeight<=41&&point.X+fixture.HeaderContent.ActualWidth<layout.ActualWidth-145,
                            $"{width}DIP菜单保持在40DIP顶栏且避开窗口按钮");
                        Invoke((System.Windows.Controls.Button)fixture.HeaderContent);
                        await Task.Delay(80);
                        Capture(canvas,Path.Combine(directory,$"plugin-header-{width}.png"));
                    }
                    Check(fixture.Clicks==2,"顶栏菜单保留可点击交互");
                    Method("ShowHostedPage").Invoke(shell,[new Border()]);
                    Check(headerSlot.Content is null&&!fixture.Hosted,"切换不提供顶栏的插件清空旧菜单并恢复所有权");
                    Method("ShowHostedPage").Invoke(shell,[fixture]);shell.Navigate("history");
                    Check(headerSlot.Content is null&&!fixture.Hosted,"返回普通页面清理顶栏引用");
                    Invoke(investment);await Task.Delay(80);menu=Menu(shell)!;
                    var disabled=(NavigationViewItem)investment.MenuItems[0]!;disabled.IsEnabled=false;
                    await Task.Delay(50);
                    Check(!menu.Items.OfType<MenuItem>().First().IsEnabled,"浮层保留原菜单的禁用状态");
                    disabled.IsEnabled=true;
                    Capture(menu,Path.Combine(directory,"investment-flyout.png"));
                    Capture(canvas,Path.Combine(directory,"collapsed-shell.png"));
                    shell.PrepareLock();
                    Check(Menu(shell) is null,"锁定时关闭浮层并清除菜单引用");
                }
                catch(Exception error){failure=error;}
                finally{shell.PrepareLock();host.Close();shell.Close();app.Shutdown();}
            };
            app.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(failure is not null)throw new InvalidOperationException("折叠导航验证失败。",failure);
    }

    private static MethodInfo Method(string name)=>typeof(MainWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
    private sealed class HeaderFixture:UserControl,IPluginShellHeader
    {
        private readonly StackPanel body=new();
        public FrameworkElement HeaderContent{get;}=new System.Windows.Controls.Button{Content="合成插件菜单",Height=32,Padding=new Thickness(12,4,12,4)};
        public bool Hosted{get;private set;}
        public int Clicks{get;private set;}
        public HeaderFixture(){Content=body;body.Children.Add(HeaderContent);((System.Windows.Controls.Button)HeaderContent).Click+=(_,_)=>Clicks++;}
        public void SetHeaderHosted(bool hosted)
        {if(hosted)body.Children.Remove(HeaderContent);else if(!body.Children.Contains(HeaderContent))body.Children.Add(HeaderContent);Hosted=hosted;}
    }
    private static ContextMenu? Menu(MainWindow shell)=>(ContextMenu?)typeof(MainWindow)
        .GetField("compactNavigationMenu",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(shell);
    private static void Invoke(UIElement item)
    {
        var peer=UIElementAutomationPeer.CreatePeerForElement(item)??throw new Exception("缺少控件自动化入口。");
        if(peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke){invoke.Invoke();return;}
        // WPF-UI 分类仅公开 ExpandCollapse 模式；执行其真实 OnClick 以覆盖展开态的内部切换及 Click 路由。
        if(item is NavigationViewItem navigationItem)
        {typeof(NavigationViewItem).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(navigationItem,null);return;}
        throw new Exception("控件不支持调用。");
    }
    private static void Check(bool condition,string scenario)
    {if(!condition)throw new Exception(scenario);Console.WriteLine("PASS 折叠菜单："+scenario);}
    private static void Capture(FrameworkElement element,string path)
    {
        element.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=File.Create(path);encoder.Save(output);
    }
}
