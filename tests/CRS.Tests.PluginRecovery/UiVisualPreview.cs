using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRS.Plugin.Wpf;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Presentation;

namespace CRS.Tests.PluginRecovery;

/// <summary>仅用合成的空视图模型渲染插件 UI；绝不访问后台管道或用户库。</summary>
internal static class UiVisualPreview
{
    public static void RenderHolders(string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(directory);
        foreach(var width in new[]{640,1040,1280})
        {
            var model=new ExitAlertViewModel(new WorkerEtfWorkspace(),default);
            var document=new CRS.Plugins.NationalTeamWatch.Domain.AlertDocument(new('A',64),"SSE","510300","https://www.sse.com.cn/synthetic-holder.pdf",new('B',64),"2026-10-27T10:00:00+08:00");
            model.ProofDocuments.Add(document);model.ProofDocument=document;
            for(var i=0;i<2;i++)model.ProofHoldings.Add(new("synthetic-"+i,"SSE","510300","synthetic-legal-"+i,"合成登记法人"+i,"合成账户"+i,new(2025,12,31),
                new(2026,3,31,18,0,0,TimeSpan.FromHours(8)),DateTimeOffset.Parse(document.FirstSeenAt),12345+i,document.Url,document.Hash,"合成物理第75页","synthetic-reviewer",i==1,true));
            model.HolderCandidateStatus="合成布局验证 · 待核验候选 · 报告期2025-12-31；公告日期2026-03-31；准确时刻未知。候选不会自动批准。";
            for(var i=1;i<=11;i++)model.HolderCandidates.Add(new(i,i==1?"合成登记持有人甲有限责任公司":i==2?"合成登记持有人乙有限责任公司":"合成持有人"+i,"合成跨行\n持有人",37858474974-i,42.62m,"合成物理第75页",null));
            var page=new ExitAlertPage(false){DataContext=model};
            var window=new Window{Content=page,Width=width,Height=960,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
            try
            {
                window.Show();window.UpdateLayout();
                Find<TabControl>(page).Items.OfType<TabItem>().Single(x=>Equals(x.Header,"ETF原件与人工核验")).IsSelected=true;
                var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                var candidateGrid=All(page).OfType<DataGrid>().Single(x=>ReferenceEquals(x.ItemsSource,model.HolderCandidates));
                if(candidateGrid.Columns[2].ActualWidth<149||candidateGrid.Columns[3].ActualWidth<124
                    ||candidateGrid.Columns.Sum(x=>x.ActualWidth)>candidateGrid.ActualWidth)
                    throw new InvalidOperationException("持有人候选的数值列被窄视口压缩或溢出。");
                var bitmap=new RenderTargetBitmap(width,960,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var stream=File.Create(Path.Combine(directory,$"holders-{width}.png")))encoder.Save(stream);
                var proofGrid=All(page).OfType<DataGrid>().Single(x=>ReferenceEquals(x.ItemsSource,model.ProofHoldings));
                proofGrid.BringIntoView();
                var proofFrame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>proofFrame.Continue=false));Dispatcher.PushFrame(proofFrame);window.UpdateLayout();
                CaptureGrid(proofGrid,Path.Combine(directory,$"holding-status-{width}.png"));
            }
            finally{window.Close();}
        }
        Console.WriteLine("PASS 持有人候选及核验标记：640/1040/1280 DIP真实窗口6张合成渲染，不连接后台。");
    }
    private static void CaptureGrid(DataGrid grid,string path)
    {
        var parent=(Panel)grid.Parent;var index=parent.Children.IndexOf(grid);var context=grid.DataContext;
        var local=grid.ReadLocalValue(FrameworkElement.DataContextProperty);var width=grid.ActualWidth;var height=grid.ActualHeight;
        parent.Children.Remove(grid);grid.DataContext=context;
        var window=new Window{Content=grid,Width=width,Height=height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(grid.ActualWidth),(int)Math.Ceiling(grid.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(grid);
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            if(pixels.Distinct().Count()<8)throw new InvalidOperationException("持仓表截图为空，不能接受。");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
        }
        finally{window.Content=null;window.Close();parent.Children.Insert(index,grid);if(local==DependencyProperty.UnsetValue)grid.ClearValue(FrameworkElement.DataContextProperty);else grid.SetValue(FrameworkElement.DataContextProperty,local);}
    }
    private static T Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)return found;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            try{return Find<T>(VisualTreeHelper.GetChild(root,i));}catch(InvalidOperationException){}
        }
        throw new InvalidOperationException("视觉元素未找到："+typeof(T).Name);
    }
    private static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        yield return root;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in All(VisualTreeHelper.GetChild(root,i)))yield return item;
    }
    public static void RenderNav(string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);Directory.CreateDirectory(directory);
        var workspace=new WorkerEtfWorkspace();var frames=0;
        foreach(var width in new[]{640,1040,1280})
        foreach(var scale in new[]{1.0,1.25,1.5})
        {
            var model=new NavStudyViewModel(workspace,workspace,default)
            {From=new(2026,10,1),Through=new(2026,10,23),Selected=new("SZSE","159915","合成创业板ETF","fixture",true,"fixture","fixture",0)};
            model.Funds.Add(model.Selected);model.Caption="159915 · 合成资料布局验证 · 非正式金融研究";
            model.Status="合成状态：完整、缺同日NAV及可比性不足分别呈现，未知金额保持空值。";
            model.Rows.Add(new(new(new(2026,10,23),1,1000000,2701800,"Complete",new('A',64))));
            model.Rows.Add(new(new(new(2026,10,23),5,5000000,null,"NAV Unavailable",new('A',64))));
            model.Rows.Add(new(new(new(2026,10,23),20,null,null,"UnverifiedComparability",new('A',64))));
            // 不挂载Loaded行为，所有显示值均为合成布局数据，不访问后台。
            var view=new NavStudyPage(model,false);var root=new Border{Child=view,Background=(Brush)System.Windows.Application.Current.FindResource("BrandCanvasBrush")};
            var window=new Window{Content=root,Width=width,Height=760,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
            try
            {
                window.Show();window.UpdateLayout();
                var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
                var grid=(DataGrid)view.FindName("StudyGrid");
                if(grid.Columns.Skip(2).Any(x=>x.ActualWidth<100))throw new InvalidOperationException("NAV数值/状态列在实际窗口中被压缩。");
                var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(760*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(directory,$"nav-{width}-{(int)(scale*100)}.png"));encoder.Save(stream);frames++;
            }
            finally{window.Content=null;window.Close();}
        }
        Console.WriteLine($"PASS NAV页面：{frames}张原生WPF合成截图，640/1040/1280 DIP、100/125/150%渲染密度，不访问后台。");
    }
    public static void RenderWorkspace(string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);
        Directory.CreateDirectory(directory);
        var frames=0;
        var measurements=new List<object>();
        var workspace=new WorkerEtfWorkspace();
        foreach(var width in new[]{640,1040,1280})
        foreach(var scale in new[]{1.0,1.25,1.5})
        {
            var model=new EtfViewModel(workspace,CancellationToken.None){Status="合成布局验证 · 当前监控24只；部分披露待核验，完整交易日历与NAV金额仍待核验。"};
            for(var i=0;i<8;i++)model.Rows.Add(new("SSE","SYNTHETIC"+i,"合成ETF "+i,"合成指数","市场观察",new(2026,10,27),123456789m,null,null,null,"待核验",8));
            var view=new NationalTeamWorkspacePage([
                new("etf-monitor","holdings","ETF份额与市场成交","ETF份额与机构披露分开，市场份额变动不等于国家队交易",()=>new EtfPage(model,false)),
                new("direct-equity","holdings","机构直接持股","具名持仓以披露证据为准",()=>new EquityPage(new EquityViewModel(workspace,CancellationToken.None),false)),
                new("industry-pe","industry","全行业公布PE","全行业公布PE独立研究",()=>new TextBlock{Text="行业公布PE预览"}),
                new("sector-index","industry","行业样本指数与PB","样本指数与PB独立研究",()=>new TextBlock{Text="样本指数预览"})]);
            view.OpenRoute("etf-monitor");
            view.SetHeaderHosted(true);var layout=new Grid();layout.RowDefinitions.Add(new(){Height=new GridLength(40)});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
            var slot=new ContentControl{Content=view.HeaderContent,HorizontalContentAlignment=HorizontalAlignment.Left,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,150,0)};
            layout.Children.Add(slot);Grid.SetRow(view,1);layout.Children.Add(view);
            var root=new Border{Background=(Brush)System.Windows.Application.Current.FindResource("BrandCanvasBrush"),Child=layout};
            root.Measure(new Size(width,760));root.Arrange(new Rect(0,0,width,760));root.UpdateLayout();
            var tabs=All(view).OfType<TabControl>().First();var page=All(view).OfType<EtfPage>().Single();
            var details=(System.Windows.Controls.Primitives.ToggleButton)page.FindName("EtfDetailsToggle");
            var limit=width<800?160:125;
            if(tabs.TranslatePoint(new Point(),page).Y>limit)throw new InvalidOperationException("紧凑页头仍占用过多正文高度。");
            measurements.Add(new{Width=width,Scale=scale,ShellMenuHeight=40,PageHeaderHeight=tabs.TranslatePoint(new Point(),page).Y,ContentHeight=tabs.ActualHeight,Source="SyntheticWpfLayout"});
            if(scale==1)
            {
                details.IsChecked=true;root.UpdateLayout();
                if(((Border)page.FindName("EtfDetailsPanel")).Visibility!=Visibility.Visible)throw new InvalidOperationException("完整研究说明不可展开。");
                details.IsChecked=false;root.UpdateLayout();
            }
            var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(760*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(directory,$"workspace-{width}-{(int)(scale*100)}.png"));encoder.Save(stream);frames++;
        }
        // 单独打开通用菜单核验弹层，不挂载任何带Loaded业务行为的页面。
        File.WriteAllText(Path.Combine(directory,"layout-measurements.json"),System.Text.Json.JsonSerializer.Serialize(measurements,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        var navigation=new PluginSectionNavigation(
            [new("holdings","资金与持仓"),new("industry","行业与估值")],
            [new("etf","holdings","ETF份额与市场成交","ETF份额与机构披露分开"),new("equity","holdings","机构直接持股","以具名披露为准"),
             new("pe","industry","全行业公布PE","全行业公布PE独立研究"),new("index","industry","行业样本指数与PB","样本指数与PB独立研究")]);
        navigation.Select("etf");
        var window=new Window{Content=navigation,Width=640,Height=100,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();
            foreach(var (category,index) in new[]{("holdings",0),("industry",1)})
            {
                navigation.OpenCategory(category);
                var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
                var headerItem=(MenuItem)((Menu)navigation.Content).Items[index];
                var menu=(FrameworkElement)((System.Windows.Controls.Primitives.Popup)headerItem.Template.FindName("PART_Popup",headerItem)).Child;menu.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),(int)Math.Ceiling(menu.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(menu);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(directory,$"menu-{category}.png"));encoder.Save(stream);headerItem.IsSubmenuOpen=false;
            }
        }
        finally{window.Close();}
        Console.WriteLine($"PASS 顶部菜单工作台：{frames}张原生WPF合成行截图，640/1040/1280 DIP、100/125/150%渲染密度；另2张原生菜单弹层；不调用后台。");
    }

    public static void Render(string directory)
    {
        PluginUiResources.Validate(System.Windows.Application.Current.Resources);
        Directory.CreateDirectory(directory);
        var frames=0;
        var workspace=new WorkerEtfWorkspace();
        foreach (var (name, factory) in new (string, Func<FrameworkElement>)[]
        {
            ("etf", ()=>new EtfPage(new EtfViewModel(workspace,CancellationToken.None),false)),
            ("equity", ()=>new EquityPage(new EquityViewModel(workspace,CancellationToken.None),false)),
            ("exit-alert", ()=>new ExitAlertPage(false)
            {
                DataContext=new ExitAlertViewModel(workspace,CancellationToken.None)
            })
        })
        {
            foreach (var width in new[] {1040,1280,1440})
            foreach (var scale in new[] {1.0,1.25,1.5})
            {
                const int height=760;
                // 不挂到真实窗口，也不触发 Loaded，防止任何后台取数。
                var view=factory();
                var root=new Border
                {
                    Background=(Brush)System.Windows.Application.Current.FindResource("BrandCanvasBrush"),
                    Child=view
                };
                root.Measure(new Size(width,height));
                root.Arrange(new Rect(0,0,width,height));
                root.UpdateLayout();
                if (view.ActualWidth < width-2 || view.ActualHeight < height-2)
                    throw new InvalidOperationException(name+" 插件页面不能填满给定视口。");
                var bitmap=new RenderTargetBitmap(
                    (int)(width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var suffix=((int)(scale*100)).ToString();
                using var stream=File.Create(Path.Combine(directory,$"{name}-{width}-{suffix}.png"));
                encoder.Save(stream);
                frames++;
            }
        }
        Console.WriteLine($"PASS 国家队三类 WPF 页面：{frames} 张空状态合成截图；不调用后台服务。");
    }
}
