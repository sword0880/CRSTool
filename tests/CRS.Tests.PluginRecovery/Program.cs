using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CRS.PluginHost;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;
using CRS.Tests.PluginRecovery;

/// <summary>新架构只核验UI包和服务查询；旧桌面数据库升级场景由Worker恢复验证替代。</summary>
internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        if(args is ["--configuration-live"])
        {try{ConfigurationAsync().GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
        if(args is ["--official-live"])
        {try{OfficialAsync().GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        if(args is ["--etf-report-visual-preview",var etfReportAcceptance,var etfReportDirectory])
        {
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                EtfReportVisualPreview.Render(etfReportAcceptance,etfReportDirectory);
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;
        }
        if(args is ["--ui-visual-preview" or "--workspace-visual-preview" or "--nav-visual-preview" or "--holder-visual-preview",var previewDirectory])
        {
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/BrandPalette.xaml")});
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {Source=new Uri("pack://application:,,,/CRS.Desktop.Wpf;component/Styles/SharedStyles.xaml")});
                if(args[0]=="--holder-visual-preview") UiVisualPreview.RenderHolders(previewDirectory);
                else if(args[0]=="--nav-visual-preview") UiVisualPreview.RenderNav(previewDirectory);
                else if(args[0]=="--workspace-visual-preview") UiVisualPreview.RenderWorkspace(previewDirectory);
                else UiVisualPreview.Render(previewDirectory);
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
            return;
        }
        app.Startup+=async(_,_)=>{try{await RunAsync(args);app.Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);app.Shutdown(1);}};
        app.Run();
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);Console.WriteLine("PASS UI只读："+message);}
    private static async Task ConfigurationAsync()
    {
        // 仅通过管道核验实际安装服务的目录，不修改用户监控状态或打开市场库。
        var rows=await new WorkerEtfWorkspace().ConfigurationAsync(default);
        Check(rows.Count>=24,"实际LocalService提供完整候选目录，不把表格可见行数当作监控上限");
        Console.WriteLine(JsonSerializer.Serialize(new{Candidates=rows.Count,Active=rows.Count(x=>x.Active)}));
    }
    private static async Task OfficialAsync()
    {
        // 真实官网事实必须来自已安装的服务，验收工具不创建桌面下载器或写库连接。
        var reader=new WorkerEtfWorkspace();var rows=await reader.OverviewAsync(default);var evidence=await reader.EvidenceAsync(default);
        Check(rows.Count>=5 && evidence.Any(x=>x.Market=="SSE") && evidence.Any(x=>x.Market=="SZSE"),"真实LocalService已联网采集两市公开ETF事实");
        Console.WriteLine(JsonSerializer.Serialize(new{Pool=rows.Count,Revisions=evidence.Count,Markets=evidence.Select(x=>x.Market).Distinct(),Dates=evidence.Select(x=>x.TradeDate).Distinct(),Status=reader.NetworkStatus}));
    }
    private static async Task RunAsync(string[] args)
    {
        if(args.Length!=1)throw new ArgumentException("需要Publish-Plugins生成的Plugins目录。");
        var release=Path.GetFullPath(args[0]);var root=Path.Combine(Path.GetTempPath(),"CRS-UiOnly",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var index=JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(Path.Combine(release,"trusted-plugins.json")),PackageVerifier.Json)!;
        var package=index.Single(x=>x.Directory.StartsWith("crs.national-team/",StringComparison.Ordinal));
        var manifest=PackageVerifier.Verify(release,package,new(1,0,0));
        Check(manifest.RequestedCapabilities.SequenceEqual(new[]{"navigation"}),"ETF桌面只申请导航能力");
        var json=JsonDocument.Parse(File.ReadAllText(PackageVerifier.Resolve(Path.Combine(release,package.Directory),"plugin.json")));
        Check(json.RootElement.GetProperty("backgroundEntry").GetProperty("assembly").GetString()=="CRS.Plugins.NationalTeam.Jobs.dll","同一份清单同时绑定UI与无界面入口");
        var references=typeof(NationalTeamPluginEntry).Assembly.GetReferencedAssemblies();
        Check(!references.Any(x=>x.Name is "Microsoft.Data.Sqlite" or "CRS.Plugins.NationalTeam.Jobs"),"UI程序集不引用采集或市场库实现");
        var storage=Path.Combine(root,"storage");
        await using(var runtime=new PluginRuntime(release,storage))
        {
            await runtime.StartAsync();Check(runtime.Statuses.All(x=>x.State==PluginState.Started),"实际UI宿主加载双入口包中的页面入口");
            Check(!Directory.EnumerateFiles(storage,"*.db",SearchOption.AllDirectories).Any(),"UI会话启动不创建任何桌面任务库或市场库");
            var nationalPages=runtime.Pages.Where(x=>x.PluginId=="crs.national-team").ToArray();
            Check(nationalPages.Length==1&&nationalPages[0].Contribution.RouteKey=="national-team-workspace","国家队只提供分类工作台，不注册旧平铺入口");
            var page=(CRS.Plugins.NationalTeamWatch.Presentation.NationalTeamWorkspacePage)nationalPages[0].Contribution.CreateView();
            Check(page.SelectedRouteKey is null,"工作台初始不打开子页面或发出数据查询");
            page.OpenRoute("etf-monitor");
            Check(page.SelectedRouteKey=="etf-monitor","ETF页面从工作台内部按需创建");
            var window=new Window{Content=page,Width=1100,Height=760,ShowInTaskbar=false};
            try
            {
                window.Show();
                foreach(var route in new[]{"etf-monitor","etf-institution-holdings","direct-equity","nav-study","industry-pe","sector-index"})
                {
                    page.OpenRoute(route);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Check(page.SelectedRouteKey==route&&page.ActualWidth>0&&page.ActualHeight>0,"工作台内部页面完成真实WPF创建与布局："+route);
                }
            }
            finally{window.Content=null;window.Close();}
            await runtime.DisposeAsync();Check(runtime.Pages.Count==0,"退出UI只释放展示会话");
        }
        // 用有效哈希包装带旧调度能力的清单，确保拒绝来自能力边界而非篡改检测。
        var rejectedRoot=Path.Combine(root,"rejected");var destination=Path.Combine(rejectedRoot,package.Directory);Directory.CreateDirectory(destination);
        foreach(var file in Directory.EnumerateFiles(Path.Combine(release,package.Directory),"*",SearchOption.AllDirectories))
        {var target=Path.Combine(destination,Path.GetRelativePath(Path.Combine(release,package.Directory),file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);}
        File.WriteAllText(Path.Combine(destination,"plugin.json"),JsonSerializer.Serialize(manifest with{RequestedCapabilities=["navigation","schedules"]},PackageVerifier.Json));
        var hashes=new Dictionary<string,string>(package.Sha256){["plugin.json"]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(destination,"plugin.json"))))};
        var denied=false;try{PackageVerifier.Verify(rejectedRoot,package with{Sha256=hashes},new(1,0,0));}catch(InvalidDataException){denied=true;}
        Check(denied,"拥有有效哈希的旧桌面调度能力也被拒绝");
        Console.WriteLine("UI只读与双入口验证完成；Worker强杀恢复、持久命令和业务去重由CRS.Tests.WorkerEtf验证。");
    }
}
