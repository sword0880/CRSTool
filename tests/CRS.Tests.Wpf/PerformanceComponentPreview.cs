using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.DesktopClient.ViewModels;
using CRS.DesktopClient.Views.Pages;
using CRS.DesktopClient.Views.Pages.Performance;
using LiveChartsCore.SkiaSharpView.WPF;

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
                var app=new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
                performance.UpdateAnnualAssets(
                    [new(2023, null, false, [], null),
                    new(2024, new(2024, 12, 31), false,
                        [new("FUTU", 130000, [new("HKD", 142000)], 1, 1, false),
                            new("IBKR", 160000, [new("USD", 22400)], 1, 1, false)], 290000),
                    new(2025, new(2025, 12, 31), false,
                        [new("FUTU", 185000, [new("HKD", 203000)], 1, 1, false),
                            new("IBKR", 240000, [new("USD", 33600)], 1, 1, false)], 425000),
                    new(2026, new(2026, 10, 9), false,
                        [new("FUTU", null, [], 0, 1, false),
                            new("IBKR", 270000, [new("USD", 37900)], 1, 1, false)], null)], 2026);
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
                CheckToolbar(performancePage,tabs,performance,directory);
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
                        var height = name == "accounts" ? 740 : 600;
                        var root=new Border{Background=(Brush)app.FindResource("BrandCanvasBrush"),Child=view};
                        root.Measure(new(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
                        if(view.ActualWidth<width-2||view.ActualHeight<height-2)
                            throw new InvalidOperationException($"绩效 {name} 子视图没有填满视口：{view.ActualWidth}x{view.ActualHeight}");
                        var tables=Collect<DataGrid>(view).ToArray();
                        if (name == "accounts")
                        {
                            var accountTable = (DataGrid)view.FindName("AccountTable");
                            var totalsTable = (DataGrid)view.FindName("AccountTotalsTable");
                            var assetsChart = (CartesianChart)view.FindName("AnnualAssetsChart");
                            var annualTable = (DataGrid)view.FindName("AnnualAssetTable");
                            var latestValue = (TextBlock)view.FindName("LatestAssetValue");
                            var assetVisibility = (Button)view.FindName("LatestAssetVisibilityButton");
                            var details = (Expander)view.FindName("AccountDetailsPanel");
                            var showAll = (CheckBox)view.FindName("ShowAccountsWithoutAmounts");
                            if (assetsChart.ActualHeight < 230 || assetsChart.Series.Count() != 3
                                || assetsChart.YAxes.Single().MinLimit != 0)
                                throw new InvalidOperationException("账户汇总的年度资产图未显示或未绑定两家券商和总额折线。");
                            if (annualTable.Items.Count != 4 || latestValue.Text != "¥ ••••••" || details.IsExpanded
                                || latestValue.TranslatePoint(new(0, 0), root).Y >= assetsChart.TranslatePoint(new(0, 0), root).Y
                                || assetsChart.TranslatePoint(new(0, 0), root).Y >= annualTable.TranslatePoint(new(0, 0), root).Y)
                                throw new InvalidOperationException("账户汇总应依次显示最新规模、趋势、年度表，默认折叠账户明细。");
                            if (assetVisibility.Visibility != Visibility.Visible || assetVisibility.Command?.CanExecute(null) != true)
                                throw new InvalidOperationException("有金额时应可以主动显示。");
                            assetVisibility.Command.Execute(null);
                            if (latestValue.Text != $"¥ {270000m:N2}" || !Equals(assetVisibility.ToolTip, "隐藏最新净资产金额"))
                                throw new InvalidOperationException("点击小眼睛后金额或提示未更新。");
                            assetVisibility.Command.Execute(null);
                            if (latestValue.Text != "¥ ••••••")
                                throw new InvalidOperationException("再次点击小眼睛后金额未隐藏。");
                            details.IsExpanded = true;
                            root.UpdateLayout();
                            if (accountTable.Items.Count != 2 || accountTable.Items.Groups?.Count != 2 || totalsTable.Items.Count != 3)
                                throw new InvalidOperationException("账户汇总未显示分组、合计或默认隐藏无金额账户。");
                            showAll.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                            root.UpdateLayout();
                            if (accountTable.Items.Count != 3 || totalsTable.Items.Count != 3)
                                throw new InvalidOperationException("勾选后未恢复无金额账户，或合计发生变化。");
                            showAll.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                            details.IsExpanded = false;
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
                        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                        Window? chartHost = null;
                        if (name == "accounts")
                        {
                            chartHost = new Window
                            {
                                Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Content = root
                            };
                            chartHost.Show();
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        }
                        image.Render(root);
                        if (chartHost is not null)
                        {
                            performance.ToggleLatestAssetAmountCommand.Execute(null);
                            chartHost.Close();
                            if (performance.ShowLatestAssetAmount)
                                throw new InvalidOperationException("离开账户汇总后仍保留金额显示状态。");
                        }
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                        using var file=File.Create(Path.Combine(directory,$"performance-{name}-{width}.png"));
                        encoder.Save(file);
                        root.Child=null;
                    }
                }
                Console.WriteLine("PASS 绩效组件化：紧凑筛选区及窄屏换行、账户汇总直达和分组，以及七类绩效子视图的合成截图。");
            }
            catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();
        if(failure is not null)throw failure;
    }

    private static void CheckToolbar(PerformancePage page,TabControl tabs,PerformanceViewModel model,string directory)
    {
        var controls=(StackPanel)page.FindName("PerformanceControls");
        if(controls.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("账户汇总页仍显示绩效筛选区。");
        tabs.SelectedIndex=0;
        model.From=new(2025,11,9);model.To=new(2026,11,10);
        var toolbar=(WrapPanel)page.FindName("PerformanceToolbar");
        var summary=(TextBlock)page.FindName("PerformanceSummary");
        var from=(DatePicker)page.FindName("PerformanceFrom");
        var to=(DatePicker)page.FindName("PerformanceTo");
        var calculate=(Button)page.FindName("CalculateButton");
        page.Background=(Brush)Application.Current.FindResource("BrandSurfaceBrush");
        foreach(var width in new[]{1040,620})
        {
            page.Measure(new(width,600));page.Arrange(new Rect(0,0,width,600));page.UpdateLayout();
            var action=calculate.TranslatePoint(new Point(),toolbar);
            if(controls.Visibility!=Visibility.Visible||summary.Text!=model.Summary||calculate.Command!=model.CalculateCommand
                ||from.TranslatePoint(new Point(),toolbar).Y!=to.TranslatePoint(new Point(),toolbar).Y
                ||action.X+calculate.ActualWidth>toolbar.ActualWidth+1
                ||(width==1040?controls.ActualHeight>76||action.Y>=32:action.Y<32))
                throw new InvalidOperationException($"绩效筛选区在 {width} DIP 下未正确压缩或换行。");
            var image=new RenderTargetBitmap(width,600,96,96,PixelFormats.Pbgra32);image.Render(page);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var file=File.Create(Path.Combine(directory,$"performance-toolbar-{width}.png"));encoder.Save(file);
        }
        var initialSummary=model.Summary;
        var summaryHeight=summary.ActualHeight;
        model.Summary=string.Join(" ",Enumerable.Repeat(initialSummary,4));page.UpdateLayout();
        if(summary.ActualHeight<=summaryHeight)throw new InvalidOperationException("长状态提示没有按可用宽度换行。");
        model.Summary=initialSummary;
    }

    private static IEnumerable<T> Collect<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T typed)yield return typed;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Collect<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
