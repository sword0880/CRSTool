using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using CRS.PluginHost;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>升级恢复验证使用独立进程和独立公开数据库，不读取用户保险库。</summary>
internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--worker")
        {try{Worker(args).GetAwaiter().GetResult();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
        var app=new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary {Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Startup+=async (_,_)=>{try{await Run(args);app.Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);app.Shutdown(1);}};app.Run();
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static async Task Worker(string[] args)
    {
        var release=args[1];var storage=args[2];var step=args[3];
        if(step=="new-fact")
        {
            var layout=new PluginStorageLayout(storage,"crs.national-team");using var lease=layout.AcquireLease();
            var active=layout.Coordinator.Active()!;var paths=layout.Paths(active.PackageVersion,active.DbGeneration,CancellationToken.None);
            var raw=new RawEvidenceStore(paths.Raw,paths.BeforeWrite);using var repository=new MarketRepository(paths.Data,raw,paths.BeforeWrite);
            var hash=raw.Save(Encoding.UTF8.GetBytes("候选进程新增的合成证据"));var fetch=repository.RecordFetch(hash,"https://www.sse.com.cn/");
            repository.ImportShares(fetch,[new(new("SSE","510300"),new(2026,9,3),102,"份")],CancellationToken.None);
            // 通知父进程后保持连接，让强杀发生在存在已提交WAL和新事实的场景。
            Console.WriteLine("FACT|"+hash);Console.Out.Flush();Thread.Sleep(Timeout.Infinite);return;
        }
        await new PluginUpgradeService(release,storage,point=>
        {
            if(point!=step)return;
            Console.WriteLine("CHECKPOINT|"+point);Console.Out.Flush();Thread.Sleep(Timeout.Infinite);
        }).PrepareAsync("crs.national-team","1.0.1");
    }
    private static async Task<string> KillAt(string release,string storage,string step)
    {
        var start=new ProcessStartInfo("dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{typeof(Program).Assembly.Location,"--worker",release,storage,step})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动隔离升级进程。");
        var errors=process.StandardError.ReadToEndAsync();
        try
        {
            var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(40));
            if(line is null)throw new InvalidOperationException("升级子进程提前退出："+await errors);
            Check(line.StartsWith(step=="new-fact"?"FACT|":"CHECKPOINT|",StringComparison.Ordinal),"独立进程到达断点 "+step);
            // 仅结束本测试创建的子进程，不触碰用户应用或任何系统服务。
            process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));return line;
        }
        finally {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
    }
    private static void Seed(string storage)
    {
        var layout=new PluginStorageLayout(storage,"crs.national-team");var raw=new RawEvidenceStore(layout.Raw);
        using var repository=new MarketRepository(layout.DataRoot,raw);
        var hash=raw.Save(Encoding.UTF8.GetBytes("升级前官方原件合成样例"));var fetch=repository.RecordFetch(hash,"https://www.sse.com.cn/");
        repository.ImportCalendar(fetch,Enumerable.Range(1,3).Select(day=>("SSE",new DateOnly(2026,9,day))).ToList(),CancellationToken.None);
        repository.ImportShares(repository.RecordFetch(hash,"https://www.sse.com.cn/"),[new(new("SSE","510300"),new(2026,9,1),100,"份"),new(new("SSE","510300"),new(2026,9,2),101,"份")],CancellationToken.None);
    }
    private static async Task VerifyPage(HostedPage page,CancellationToken token)
    {
        System.Windows.Application.Current.Dispatcher.VerifyAccess();token.ThrowIfCancellationRequested();
        var view=page.Contribution.CreateView();var window=new Window {Content=view,Width=1200,Height=800,ShowActivated=false,ShowInTaskbar=false,Opacity=0};
        try{window.Show();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();Check(view.ActualHeight>0,"候选真实WPF页面加载");}
        finally{window.Content=null;window.Close();}
    }
    private static async Task Run(string[] args)
    {
        if(args.Length!=1)throw new ArgumentException("请传入含ETF插件的发布Plugins目录。");
        var root=Path.Combine(Path.GetTempPath(),"CRS-recovery-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var release=Path.Combine(root,"release");PrepareCatalog(Path.GetFullPath(args[0]),release);
        // 保持源连接打开，验证快照确实包含尚未归并到主文件的已提交WAL。
        var walStorage=Path.Combine(root,"wal-snapshot");Seed(walStorage);var walLayout=new PluginStorageLayout(walStorage,"crs.national-team");
        using(var source=new MarketRepository(walLayout.DataRoot,new RawEvidenceStore(walLayout.Raw)))
        {
            var raw=new RawEvidenceStore(walLayout.Raw);var hash=raw.Save(Encoding.UTF8.GetBytes("WAL快照的合成新增证据"));
            source.ImportShares(source.RecordFetch(hash,"https://www.sse.com.cn/"),[new(new("SSE","510300"),new(2026,9,3),103,"份")],CancellationToken.None);
            Check(File.Exists(walLayout.Database("legacy")+"-wal"),"快照源含未关闭的WAL");
            var paths=walLayout.Paths("1.0.0","legacy",CancellationToken.None);var snapshot=Path.Combine(walLayout.Backup,"wal.db");
            await new MarketStorageMaintenance(paths).SnapshotAsync(walLayout.Database("legacy"),snapshot,CancellationToken.None);
            var snapshotData=Path.Combine(root,"wal-copy");Directory.CreateDirectory(snapshotData);File.Copy(snapshot,Path.Combine(snapshotData,"market.db"));
            using var copied=new MarketRepository(snapshotData,raw);Check(copied.Points(new("SSE","510300")).Count==3,"Backup快照包含已提交的WAL事实");
        }
        foreach(var step in new[]{"Prepared","SnapshotWritten","Snapshotted","CandidateMigrated","Migrated","SwitchRecorded","PointerSwitched"})
        {
            var storage=Path.Combine(root,step);Seed(storage);await KillAt(release,storage,step);
            var layout=new PluginStorageLayout(storage,"crs.national-team");
            await using(var runtime=new PluginRuntime(release,storage,validatePage:VerifyPage))
            {
                await runtime.StartAsync();Check(runtime.Statuses.Single().State==PluginState.Started,"强杀后宿主正常启动 "+step);
                Check(layout.Coordinator.Read()!.Stage==(step=="PointerSwitched"?UpgradeStage.Committed:UpgradeStage.RolledBack),"恢复到明确版本状态 "+step);
            }
            using var old=new MarketRepository(layout.DataRoot,new RawEvidenceStore(layout.Raw));Check(old.Points(new("SSE","510300")).Count==2,"旧库事实保留 "+step);
        }
        // 页面失败或缺少真实页面核验都不能被当作升级成功，旧库仍可再次启动。
        foreach(var failure in new[]{"page-failure","missing-validator"})
        {
            var storage=Path.Combine(root,failure);Seed(storage);await new PluginUpgradeService(release,storage).PrepareAsync("crs.national-team","1.0.1");
            Func<HostedPage,CancellationToken,Task>? validator=failure=="page-failure"?(_,_)=>Task.FromException(new InvalidDataException("模拟页面加载失败")):null;
            await using(var runtime=new PluginRuntime(release,storage,validatePage:validator))
            {await runtime.StartAsync();Check(runtime.Statuses.Single().State==PluginState.Failed,"候选页面检查失败被阻止 "+failure);}
            var layout=new PluginStorageLayout(storage,"crs.national-team");Check(layout.Coordinator.Read()!.Stage==UpgradeStage.RolledBack,"无新事实时回退指针 "+failure);
            await using var restarted=new PluginRuntime(release,storage,validatePage:VerifyPage);await restarted.StartAsync();
            Check(restarted.Statuses.Single().State==PluginState.Started && layout.Coordinator.Active()!.PackageVersion=="1.0.0","回退后旧版重新启动 "+failure);
        }
        var withFacts=Path.Combine(root,"with-facts");Seed(withFacts);
        await new PluginUpgradeService(release,withFacts).PrepareAsync("crs.national-team","1.0.1");
        var fact=await KillAt(release,withFacts,"new-fact");var factLayout=new PluginStorageLayout(withFacts,"crs.national-team");
        File.WriteAllText(Path.Combine(factLayout.Raw,fact.Split('|')[1]+".raw"),"tampered");
        await using(var failed=new PluginRuntime(release,withFacts,validatePage:VerifyPage))
        {await failed.StartAsync();Check(failed.Statuses.Single().State==PluginState.Failed,"新事实证据损坏时停止候选启动");}
        Check(factLayout.Coordinator.Read()!.Stage==UpgradeStage.RequiresRecovery && factLayout.Coordinator.Active()!.PackageVersion=="1.0.1","新事实存在时禁止自动回退旧库");
        Check(File.Exists(factLayout.Database("legacy")) && File.Exists(factLayout.Database(factLayout.Coordinator.Active()!.DbGeneration)),"新旧库和原件现场全部保留");
        using(var lease=factLayout.AcquireLease())
        {
            try{using var duplicate=factLayout.AcquireLease();throw new Exception("第二写入租约被接受");}catch(IOException){Console.WriteLine("PASS 同一插件拒绝第二写入租约");}
        }
        Console.WriteLine("插件独立进程恢复验证通过；临时目录："+root);
    }
    private static void PrepareCatalog(string source,string destination)
    {
        var index=JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(Path.Combine(source,"trusted-plugins.json")),PackageVerifier.Json)!;
        var original=index.Single(x=>x.Directory.StartsWith("crs.national-team/",StringComparison.Ordinal));var packages=new List<TrustedPackage>();
        foreach(var version in new[]{"1.0.0","1.0.1"})
        {
            var directory=Path.Combine(destination,"crs.national-team",version);Directory.CreateDirectory(directory);
            foreach(var file in original.Sha256.Keys)
            {
                var target=PackageVerifier.Resolve(directory,file);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(PackageVerifier.Resolve(Path.Combine(source,original.Directory),file),target);
            }
            var manifestPath=Path.Combine(directory,"plugin.json");var manifest=JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath),PackageVerifier.Json)!;
            File.WriteAllText(manifestPath,JsonSerializer.Serialize(manifest with{Version=version},PackageVerifier.Json));
            var hashes=Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(path=>Path.GetRelativePath(directory,path).Replace('\\','/'),path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            packages.Add(new("crs.national-team/"+version,hashes));
        }
        // 只在隔离测试目录构造开发可信索引，不修改真实发行包或用户版本指针。
        File.WriteAllText(Path.Combine(destination,"trusted-plugins.json"),JsonSerializer.Serialize(packages,PackageVerifier.Json));
    }
}
