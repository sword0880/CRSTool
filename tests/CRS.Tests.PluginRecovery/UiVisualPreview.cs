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
            model.HolderCandidateStatus="合成布局验证 · 待核验候选 · 报告期2025-12-31；公告日期2026-03-31；准确时刻未知。候选不会自动批准。";
            for(var i=1;i<=11;i++)model.HolderCandidates.Add(new(i,i==1?"合成登记持有人甲有限责任公司":i==2?"合成登记持有人乙有限责任公司":"合成持有人"+i,"合成跨行\n持有人",37858474974-i,42.62m,"合成物理第75页",null));
            var page=new ExitAlertPage(false){DataContext=model};
            var window=new Window{Content=page,Width=width,Height=960,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
            try
            {
                window.Show();window.UpdateLayout();
                Find<TabControl>(page).Items.OfType<TabItem>().Single(x=>Equals(x.Header,"ETF原件与人工核验")).IsSelected=true;
                var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);window.UpdateLayout();
                var bitmap=new RenderTargetBitmap(width,960,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(directory,$"holders-{width}.png"));encoder.Save(stream);
            }
            finally{window.Close();}
        }
        Console.WriteLine("PASS 持有人候选：640/1040/1280 DIP真实窗口3张合成渲染，不连接后台。");
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
        var workspace=new WorkerEtfWorkspace();
        foreach(var width in new[]{640,1040,1280})
        foreach(var scale in new[]{1.0,1.25,1.5})
        {
            var view=new NationalTeamWorkspacePage([
                new("etf-monitor","holdings","ETF份额与市场成交","ETF份额与机构披露分开，市场份额变动不等于国家队交易",()=>new EtfPage(new EtfViewModel(workspace,CancellationToken.None),false)),
                new("direct-equity","holdings","机构直接持股","具名持仓以披露证据为准",()=>new EquityPage(new EquityViewModel(workspace,CancellationToken.None),false)),
                new("industry-pe","industry","全行业公布PE","全行业公布PE独立研究",()=>new TextBlock{Text="行业公布PE预览"}),
                new("sector-index","industry","行业样本指数与PB","样本指数与PB独立研究",()=>new TextBlock{Text="样本指数预览"})]);
            view.OpenRoute("etf-monitor");
            var root=new Border{Background=(Brush)System.Windows.Application.Current.FindResource("BrandCanvasBrush"),Child=view};
            root.Measure(new Size(width,760));root.Arrange(new Rect(0,0,width,760));root.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)(width*scale),(int)(760*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(directory,$"workspace-{width}-{(int)(scale*100)}.png"));encoder.Save(stream);frames++;
        }
        // 单独打开通用菜单核验弹层，不挂载任何带Loaded业务行为的页面。
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
                var button=(Button)((WrapPanel)navigation.Content).Children[index];
                var menu=button.ContextMenu!;menu.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),(int)Math.Ceiling(menu.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(menu);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(directory,$"menu-{category}.png"));encoder.Save(stream);menu.IsOpen=false;
            }
        }
        finally{window.Close();}
        Console.WriteLine($"PASS 顶部菜单工作台：{frames}张原生WPF空状态截图，640/1040/1280 DIP、100/125/150%渲染密度；另2张原生菜单弹层；不调用后台。");
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
