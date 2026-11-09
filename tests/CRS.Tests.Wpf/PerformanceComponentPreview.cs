using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using CRS.DesktopClient.Views.Pages.Performance;

/// <summary>验证绩效只读子视图可独立排版，完全使用合成模型，不访问真实资料库。</summary>
internal static class PerformanceComponentPreview
{
    public static void Run(string directory)
    {
        Exception? failure=null;
        var thread=new Thread(() =>
        {
            try
            {
                var app=new System.Windows.Application();
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                var shell=new ShellViewModel(new FakeUseCases(),new SilentInteraction());
                var performance=shell.Performance;
                performance.AccountOverview = [new("富途证券", "港股账户 · 尾号 1234", new(2025, 12, 31), null, null, null)
                    { NetAssetsByCurrency = [new("HKD", 20000), new("USD", 1200)], CashByCurrency = [new("HKD", 0), new("USD", -50)] },
                    new("盈透证券（IBKR）", "美股账户 · 尾号 5678", new(2025, 11, 30), "USD", 34567, 1234),
                    new("富途证券", "待补资料账户", null, null, null, null)];
                string? route = null;
                shell.NavigateRequested += page => route = page;
                shell.Dashboard.OpenAccountOverviewCommand.Execute(null);
                var performancePage = new PerformancePage(shell);
                performancePage.Measure(new(1000, 600));
                performancePage.Arrange(new Rect(0, 0, 1000, 600));
                performancePage.UpdateLayout();
                var tabs = Collect<TabControl>(performancePage).First();
                if (route != "performance" || tabs.SelectedItem is not TabItem { Header: "账户汇总" })
                    throw new InvalidOperationException("概览未直达绩效的账户汇总页签。");
                Directory.CreateDirectory(directory);
                foreach (var (name,view) in new (string, UserControl)[]
                {
                    ("accounts",new PerformanceAccountsView()),
                    ("exposure",new PerformanceExposureView()),
                    ("history",new PerformanceHistoryView()),
                    ("workbench",new PerformanceWorkbenchView()),
                    ("charts",new PerformanceChartsView()),
                    ("manual",new PerformanceManualEntryView()),
                    ("flows",new PerformanceFlowsView())
                })
                {
                    view.DataContext=shell.Performance;
                    foreach(var width in new[]{1040,1280,1440})
                    {
                        var root=new Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=view};
                        root.Measure(new(width,600));root.Arrange(new Rect(0,0,width,600));root.UpdateLayout();
                        if(view.ActualWidth<width-2||view.ActualHeight<598)
                            throw new InvalidOperationException($"绩效 {name} 子视图没有填满视口：{view.ActualWidth}x{view.ActualHeight}");
                        var tables=Collect<DataGrid>(view).ToArray();
                        if (name == "accounts")
                        {
                            var accountTable = (DataGrid)view.FindName("AccountTable");
                            var totalsTable = (DataGrid)view.FindName("AccountTotalsTable");
                            var showAll = (CheckBox)view.FindName("ShowAccountsWithoutAmounts");
                            if (accountTable.Items.Count != 2 || accountTable.Items.Groups?.Count != 2 || totalsTable.Items.Count != 3)
                                throw new InvalidOperationException("账户汇总未显示分组、合计或默认隐藏无金额账户。");
                            showAll.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                            root.UpdateLayout();
                            if (accountTable.Items.Count != 3 || totalsTable.Items.Count != 3)
                                throw new InvalidOperationException("勾选后未恢复无金额账户，或合计发生变化。");
                            showAll.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                            root.UpdateLayout();
                        }
                        if(name is "exposure" or "history")
                        {
                            var table=tables.FirstOrDefault()??throw new InvalidOperationException("绩效只读视图缺少表格："+name);
                            if(table.Columns.Count<5||table.ItemsSource is null)
                                throw new InvalidOperationException("绩效只读视图丢失表格列或绑定："+name);
                            // 空状态不能把表头自动压缩为单个汉字；允许合理水平滚动。
                            var narrow=table.Columns.Where(column=>column.MinWidth<70 || column.ActualWidth<70).ToArray();
                            if(narrow.Length>0)
                                throw new InvalidOperationException("绩效 "+name+" 列宽过窄："+
                                    string.Join("，",narrow.Select(c=>$"{c.Header}:{c.ActualWidth:F0}/{c.MinWidth:F0}")));
                        }
                        if(name is "workbench" or "flows")
                        {
                            if(tables.Length<2||tables.Any(t=>t.ItemsSource is null))
                                throw new InvalidOperationException("绩效工作台/资金流组件丢失表格或绑定："+name);
                        }
                        if(name=="manual" && !Collect<DatePicker>(view).Any())
                            throw new InvalidOperationException("绩效资料补录缺少日期控件。");
                        if(name=="charts" && !Collect<TabControl>(view).Any())
                            throw new InvalidOperationException("绩效曲线组件丢失内部图表标签。");
                        var image=new RenderTargetBitmap(width,600,96,96,PixelFormats.Pbgra32);
                        image.Render(root);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                        using var file=File.Create(Path.Combine(directory,$"performance-{name}-{width}.png"));
                        encoder.Save(file);
                        root.Child=null;
                    }
                }
                Console.WriteLine("PASS 绩效组件化：账户汇总直达和分组，以及七类绩效子视图的合成截图。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();
        if(failure is not null)throw failure;
    }

    private static IEnumerable<T> Collect<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T typed)yield return typed;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Collect<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
