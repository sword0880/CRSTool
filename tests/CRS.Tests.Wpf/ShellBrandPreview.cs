using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views;

/// <summary>脱离窗口生命周期渲染真实主窗口布局，不启动插件、后台连接或个人保险库。</summary>
internal static class ShellBrandPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var app=new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                // 插件视觉契约只能依赖共享 WPF 资源，缺失时加载前应明确失败。
                CRS.Plugin.Wpf.PluginUiResources.Validate(app.Resources);
                var rejectedMissingResources=false;
                try { CRS.Plugin.Wpf.PluginUiResources.Validate(new ResourceDictionary()); }
                catch(InvalidOperationException) { rejectedMissingResources=true; }
                if(!rejectedMissingResources)throw new InvalidOperationException("插件 UI 契约未拒绝资源缺失。");
                var window=new MainWindow(new ShellViewModel(new FakeUseCases(),new SilentInteraction()));
                var button=window.FindName("NavigationToggleButton") as System.Windows.Controls.Button
                    ??throw new InvalidOperationException("主窗口缺少统一导航按钮。");
                var brand=window.FindName("BrandIdentity") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少品牌名称。");
                var header=window.FindName("BrandHeader") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少统一标题栏布局。");
                var pluginSurface=window.FindName("PluginSurface") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少插件承载区域。");
                var footer=window.FindName("ShellFooter") as FrameworkElement
                    ??throw new InvalidOperationException("主窗口缺少全局 Worker 状态栏。");
                var footerButtons=new[]
                {
                    "FooterHealthButton","FooterTasksButton","FooterLastSyncButton","FooterIssuesButton"
                }.Select(name=>window.FindName(name) as System.Windows.Controls.Button
                    ??throw new InvalidOperationException("全局状态栏缺少可用入口："+name)).ToArray();
                var layout=(FrameworkElement)window.Content;window.Content=null;layout.DataContext=window.DataContext;
                var surface=new System.Windows.Controls.Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=layout};
                Directory.CreateDirectory(directory);
                foreach(var width in new[]{1040,1280,1440})
                foreach(var scale in new[]{1.0,1.25,1.5})
                {
                    foreach(var expanded in new[]{true,false})
                    {
                        // 使用真实Button.Click事件而非直接改IsPaneOpen，确保唯一按钮和原导航状态同源。
                        if(window.IsNavigationExpanded!=expanded)
                            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,button));
                        surface.Measure(new(width,880));surface.Arrange(new Rect(0,0,width,880));surface.UpdateLayout();
                        if(footer.ActualHeight is < 32 or > 60 || footerButtons.Any(x=>x.Visibility!=Visibility.Visible || x.ActualWidth<=0))
                            throw new InvalidOperationException($"全局运行状态栏未正确布局: width={width}, expanded={expanded}, footer={footer.ActualHeight}");
                        if(Math.Abs(footer.TranslatePoint(new Point(0,0),layout).Y+footer.ActualHeight-880)>2)
                            throw new InvalidOperationException("底部状态栏未固定在主窗口底边。");
                        var expected=expanded?206:56;
                        if(window.IsNavigationExpanded!=expanded||Math.Abs(header.ActualWidth-expected)>1
                            ||Math.Abs(pluginSurface.Margin.Left-expected)>1
                            ||brand.Visibility!=(expanded?Visibility.Visible:Visibility.Collapsed)
                            ||brand.IsHitTestVisible||!button.IsHitTestVisible || Math.Abs(button.ActualWidth-expected)>1)
                            throw new InvalidOperationException(
                                $"品牌栏同步失败: width={width}, scale={scale}, expandedExpected={expanded}, " +
                                $"expandedActual={window.IsNavigationExpanded}, header={header.ActualWidth}, expected={expected}, " +
                                $"margin={pluginSurface.Margin.Left}, brand={brand.Visibility}, hit={brand.IsHitTestVisible}, toggleHit={button.IsHitTestVisible}");
                        // 真正执行布局后的命中测试：检查鼠标落点是否击中按钮，而非仅 RaiseEvent 模拟 Click。
                        foreach(var clickX in (expanded ? new[]{28d,80d,156d,192d} : new[]{28d}))
                        {
                            var hitPoint=button.TranslatePoint(new Point(clickX,26),layout);
                            var visual=VisualTreeHelper.HitTest(layout,hitPoint)?.VisualHit;
                            if(!IsWithin(visual,button))
                                throw new InvalidOperationException($"标题栏覆盖品牌按钮的鼠标命中区域: x={clickX}, width={width}, expanded={expanded}, hit={visual?.GetType().Name}");
                        }
                        if((string?)button.ToolTip!=(expanded?"收起侧边栏":"展开侧边栏"))
                            throw new InvalidOperationException("导航按钮悬停文案与展开状态不一致。");
                        var suffix=(expanded?"": "-collapsed")+(scale==1.0?"":"-"+((int)(scale*100)).ToString());
                        var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(180*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(surface);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output=File.Create(Path.Combine(directory,$"brand-header-{width}{suffix}.png"));encoder.Save(output);
                        if(width==1280 && scale==1.0 && expanded)
                        {
                            var full=new RenderTargetBitmap(1280,880,96,96,PixelFormats.Pbgra32);
                            full.Render(surface);
                            var footerShot=new PngBitmapEncoder();footerShot.Frames.Add(BitmapFrame.Create(full));
                            using var footerImage=File.Create(Path.Combine(directory,"shell-footer-1280.png"));footerShot.Save(footerImage);
                        }
                        if(width==1280 && scale==1.0)
                        {
                            var detail=new RenderTargetBitmap(412,128,192,192,PixelFormats.Pbgra32);detail.Render(surface);
                            var closeup=new PngBitmapEncoder();closeup.Frames.Add(BitmapFrame.Create(detail));
                            using var image=File.Create(Path.Combine(directory,$"brand-header-detail{suffix}.png"));closeup.Save(image);
                        }
                    }
                }
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,button));
                if(!window.IsNavigationExpanded)throw new InvalidOperationException("二次折叠操作未恢复展开状态。");
                Console.WriteLine("PASS 主窗口品牌栏与全局Worker底栏：1040/1280/1440宽度和96/120/144 DPI位图渲染，展开与收起、品牌区真实视觉命中、唯一折叠按钮、插件偏移、品牌图文和恢复操作。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw failure;
    }
    private static bool IsWithin(DependencyObject? node,DependencyObject expected)
    {
        for(var current=node;current is not null;current=VisualTreeHelper.GetParent(current))
            if(ReferenceEquals(current,expected))return true;
        return false;
    }

}
