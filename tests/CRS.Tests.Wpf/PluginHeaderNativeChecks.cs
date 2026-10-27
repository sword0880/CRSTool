using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;
using CRS.Plugin.Wpf;

/// <summary>真实MainWindow及原生菜单；关闭业务启动，不查询正式服务或个人保险库。</summary>
internal static class PluginHeaderNativeChecks
{
    [DllImport("user32.dll")]private static extern nint SendMessage(nint handle,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    public static void Run(string output,bool interactive=false,bool requireFocus=false)
    {
        Exception? failure=null;var passed=0;var focusVerified=false;void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS 顶栏菜单："+text);}
        var thread=new Thread(()=>
        {
            var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            foreach(var name in new[]{"BrandPalette","SharedStyles"})app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri($"pack://application:,,,/CRS.Desktop.Wpf;component/Styles/{name}.xaml")});
            var window=new MainWindow(new ShellViewModel(new FakeUseCases(),new SilentInteraction()),startWorkspace:false){Width=1040,Height=760,Title="CRS 菜单焦点隔离检查"};Directory.CreateDirectory(output);
            window.Closed+=(_,_)=>app.Shutdown();
            window.ContentRendered+=async(_,_)=>
            {
                try
                {
                    await Task.Delay(100);var fixture=new HeaderFixture();typeof(MainWindow).GetMethod("ShowHostedPage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[fixture]);window.UpdateLayout();await Task.Delay(100);
                    var navigation=(PluginSectionNavigation)fixture.HeaderContent;var categories=((Menu)navigation.Content).Items.Cast<MenuItem>().ToArray();var first=categories[0];var second=categories[1];var hwnd=new WindowInteropHelper(window).Handle;
                    if(interactive)
                    {
                        window.AddHandler(Keyboard.GotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler((_,_)=>Console.WriteLine("FOCUS "+FocusName())),true);
                        Console.WriteLine("READY 仅合成菜单，无正式服务或个人资料。");await Task.Delay(TimeSpan.FromMinutes(5));return;
                    }
                    focusVerified=GetForegroundWindow()==hwnd;
                    if(!focusVerified){Console.WriteLine("FocusVerification=Unavailable：当前执行桌面没有取得真实前台窗口，不将逻辑调用当作键盘焦点验收。");if(requireFocus)throw new KeyboardFocusUnavailableException();}
                    void Open(MenuItem item,string key){if(focusVerified)Expand(item);else navigation.OpenCategory(key);}
                    foreach(var width in new[]{1040,1280})
                    {
                        window.Width=width;window.UpdateLayout();await Task.Delay(70);
                        var point=first.PointToScreen(new(first.ActualWidth/2,first.ActualHeight/2));var hit=(int)SendMessage(hwnd,0x0084,0,Pack(point));Console.WriteLine($"WM_NCHITTEST width={width} result={hit}");
                        Check(hit==1,$"{width}DIP菜单属于客户区，点击不被窗口拖动截走");
                        Open(first,"funds");await Task.Delay(100);
                        Check(first.IsSubmenuOpen&&(!focusVerified||first.IsKeyboardFocusWithin&&!second.IsKeyboardFocused),focusVerified?"首次展开焦点保留在第一菜单":"第一菜单完整展开，键盘焦点待交互桌面验收");
                        if(focusVerified){Key(first,System.Windows.Input.Key.Down);await Task.Delay(80);Check(first.Items.Cast<MenuItem>().Any(x=>x.IsKeyboardFocused),"向下键进入第一菜单的目的地");}
                        Open(second,"industry");await Task.Delay(100);Check(second.IsSubmenuOpen&&!first.IsSubmenuOpen&&(!focusVerified||second.IsKeyboardFocusWithin),"切换第二菜单后只保留当前弹层");
                        Open(first,"funds");await Task.Delay(100);Check(first.IsSubmenuOpen&&!second.IsSubmenuOpen&&(!focusVerified||first.IsKeyboardFocusWithin),"切回第一菜单不打开相邻分类");
                        var leaf=first.Items.Cast<MenuItem>().Single();((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(leaf)!.GetPattern(PatternInterface.Invoke)!).Invoke();await Task.Delay(80);
                        Check(fixture.Selected=="etf","选择目的地同步到插件正文");
                        Open(first,"funds");await Task.Delay(80);
                        if(focusVerified){Key(first,System.Windows.Input.Key.Escape);await Task.Delay(80);Check(!first.IsSubmenuOpen&&first.IsKeyboardFocused,"Esc关闭后焦点回到本次打开分类");}else{first.IsSubmenuOpen=false;Check(!first.IsSubmenuOpen,"关闭弹层不保留悬浮内容");}
                        navigation.OpenCategory("industry");navigation.OpenCategory("funds");await Task.Delay(100);
                        Check(first.IsSubmenuOpen&&!second.IsSubmenuOpen&&(!focusVerified||first.IsKeyboardFocusWithin),"快速连续切换只保留最后菜单");first.IsSubmenuOpen=false;
                        var root=(FrameworkElement)window.Content;var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,$"header-native-{width}.png"));encoder.Save(stream);
                    }
                    var title=Descendants(window).OfType<Wpf.Ui.Controls.TitleBar>().Single();var drag=title.PointToScreen(new(title.ActualWidth-180,title.ActualHeight/2));Check((int)SendMessage(hwnd,0x0084,0,Pack(drag))==2,"剩余标题栏支持拖动窗口");
                    Check(window.Navigate("history")&&((ContentControl)window.FindName("PluginHeaderContent")).Content is null,"离开插件后清理菜单及焦点引用");
                }
                catch(Exception error){failure=error;}
                finally{window.PrepareLock();window.Close();app.Shutdown();}
            };app.Run(window);
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is KeyboardFocusUnavailableException)throw failure;if(failure is not null)throw new InvalidOperationException("顶栏菜单验证失败。",failure);Console.WriteLine(interactive?"只读焦点预览结束；未执行自动断言。":$"顶栏命中及菜单操作{passed}项通过；FocusVerification={(focusVerified?"Verified":"Unavailable")}，不连接正式后台。");
    }
    private static nint Pack(Point point)=>(nint)(((int)point.Y<<16)|((int)point.X&0xffff));
    private static void Expand(MenuItem item){var focused=item.Focus();Console.WriteLine($"EXPAND focusable={item.Focusable} enabled={item.IsEnabled} visible={item.IsVisible} focused={focused} actual={FocusName()} role={item.Role}");((IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(item)!.GetPattern(PatternInterface.ExpandCollapse)!).Expand();}
    private static void Key(MenuItem item,System.Windows.Input.Key key)=>item.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(item)!,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
    private static string FocusName()=>Keyboard.FocusedElement switch{MenuItem item=>"MenuItem:"+(item.Header is string text?text:System.Windows.Automation.AutomationProperties.GetName(item)),null=>"null",var item=>item.GetType().Name};
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private sealed class HeaderFixture:UserControl,IPluginShellHeader
    {
        private readonly StackPanel body=new();public string? Selected;
        public FrameworkElement HeaderContent{get;}=new PluginSectionNavigation([new("funds","资金与持仓"),new("industry","行业与估值")],[new("etf","funds","ETF份额与市场成交","合成导航"),new("pe","industry","行业估值","合成导航")]);
        public HeaderFixture(){Content=body;body.Children.Add(HeaderContent);((PluginSectionNavigation)HeaderContent).RouteRequested+=route=>Selected=route;}
        public void SetHeaderHosted(bool hosted){if(hosted)body.Children.Remove(HeaderContent);else if(!body.Children.Contains(HeaderContent))body.Children.Add(HeaderContent);}
    }
}
internal sealed class KeyboardFocusUnavailableException:Exception;
