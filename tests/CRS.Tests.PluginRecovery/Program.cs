using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CRS.PluginHost;
using CRS.Worker.Ipc;
using CRS.Plugins.NationalTeamWatch;

/// <summary>新架构只核验UI包和服务查询；旧桌面数据库升级场景由Worker恢复验证替代。</summary>
internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        if(args is ["--official-live"])
        {try{OfficialAsync().GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary{Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Startup+=async(_,_)=>{try{await RunAsync(args);app.Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);app.Shutdown(1);}};
        app.Run();
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);Console.WriteLine("PASS UI只读："+message);}
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
            var page=runtime.Pages.Single(x=>x.PluginId=="crs.national-team").Contribution.CreateView();
            var window=new Window{Content=page,Width=1100,Height=760,ShowInTaskbar=false};
            try{window.Show();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();Check(page.ActualWidth>0 && page.ActualHeight>0,"服务查询页面完成真实WPF布局");}
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
