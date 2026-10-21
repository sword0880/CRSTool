using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CRS.PluginHost;
// 真实STA/WPF验证：两个包由通用清单加载，测试项目不引用业务插件实现。
internal static class Program
{
 [STAThread] private static void Main(string[] args)
 {
  var app=new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
  app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary {Theme=Wpf.Ui.Appearance.ApplicationTheme.Light});
  app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
  app.Startup+=async (_,_)=>
  {
   try {await Run(args);app.Shutdown(0);}catch(Exception error){Console.Error.WriteLine(error);app.Shutdown(1);}
  };app.Run();
 }
 private static void Check(bool condition,string name) {if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
 private static async Task Run(string[] args)
 {
  if(args is ["--load-probe",var directory,"--host-assemblies",var hostDirectory])
  {
   // 复现桌面默认上下文先加载共享依赖的顺序，不能只验证测试程序自带的依赖。
   foreach(var name in new[]{"CRS.Worker.Abstractions.dll","CRS.Worker.Ipc.dll"})
   {
    var path=Path.GetFullPath(Path.Combine(hostDirectory,name));
    if(File.Exists(path))System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
   }
   args=["--load-probe",directory];
  }
  if(args is ["--load-probe",var pluginDirectory])
  {
   var isolated=Path.Combine(Path.GetTempPath(),"CRS-plugin-load-probe",Guid.NewGuid().ToString("N"));
   await using var probe=new PluginRuntime(Path.GetFullPath(pluginDirectory),isolated);
   await probe.StartAsync();
   foreach(var status in probe.Statuses)Console.WriteLine($"{status.Id}: {status.State};version={status.Version};error={status.Error};detail={status.Failure}");
   Check(probe.Statuses.Count>0&&probe.Statuses.All(x=>x.State==PluginState.Started),"可信包经真实宿主加载并启动");
   foreach(var page in probe.Pages)
   {
    var view=page.Contribution.CreateView();view.Measure(new Size(1200,800));view.Arrange(new Rect(0,0,1200,800));view.UpdateLayout();
    Check(view.ActualWidth>0&&view.ActualHeight>0,"页面构造及布局 "+page.Route);
   }
   return;
  }
  // 同AssemblyVersion但二进制不同的旧组件必须在插件运行前被拦截，否则采集点击后才MissingMethodException。
  var runningAssembly=typeof(PackageVerifier).Assembly.Location;
  PackageVerifier.EnsureLoadedAssemblyMatches(runningAssembly);
  Console.WriteLine("PASS 当前已加载程序集与可信文件字节一致");
  var changedAssembly=Path.Combine(Path.GetTempPath(),"CRS-host-shared-conflict-"+Guid.NewGuid().ToString("N")+".dll");
  try
  {
   File.Copy(runningAssembly,changedAssembly);
   // 仅附加一个字节，保留真实程序集标识和版本，模拟被重新打包的同版本DLL。
   using(var handle=new FileStream(changedAssembly,FileMode.Append,FileAccess.Write,FileShare.None))handle.WriteByte(0x5a);
   if(System.Reflection.AssemblyName.GetAssemblyName(changedAssembly).FullName!=typeof(PackageVerifier).Assembly.GetName().FullName)
    throw new InvalidOperationException("同版本冲突测试没有保留程序集身份。");
   try{PackageVerifier.EnsureLoadedAssemblyMatches(changedAssembly);throw new Exception("同版本不同字节未拒绝");}
   catch(InvalidDataException error) when(error.Message.Contains("同版本但代码不同"))
   {Console.WriteLine("PASS 同版本不同字节的程序集启动前被拒绝");}
  }
  finally {if(File.Exists(changedAssembly))File.Delete(changedAssembly);}
  var root=Path.Combine(Path.GetTempPath(),"CRS-host-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  await using(var empty=new PluginRuntime(root,Path.Combine(root,"storage"))) {await empty.StartAsync();Check(empty.Pages.Count==0,"Tax-only no-plugin startup");}
  // 用同名目录阻止临时配置文件创建，保存失败必须保留原来的内存设置。
  var blockedStorage=Path.Combine(root,"blocked-settings");Directory.CreateDirectory(Path.Combine(blockedStorage,"disabled.json.tmp"));
  await using(var blocked=new PluginRuntime(root,blockedStorage))
  {
   try {blocked.SetEnabledForNextStart("fixture.plugin",false);throw new Exception("保存失败未上报");}
   catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
   Check(blocked.IsEnabledForNextStart("fixture.plugin"),"Failed enablement write preserves next-start state");
  }
  foreach(var path in new[]{"../escape","C:/absolute","a/../../escape"})
  {try {PackageVerifier.Resolve(root,path);throw new Exception("Path accepted");}catch(InvalidDataException){Console.WriteLine("PASS path traversal "+path);}}
  foreach(var stage in Enum.GetValues<UpgradeStage>().Where(x=>x is UpgradeStage.Prepared or UpgradeStage.Snapshotted or UpgradeStage.Migrated or UpgradeStage.Switched))
  {
   var folder=Path.Combine(root,stage.ToString());Directory.CreateDirectory(folder);var journal=new UpgradeCoordinator(folder);
   var previous=new ActiveGeneration("1.0.0","old");var candidate=new ActiveGeneration("2.0.0","new");
   File.WriteAllText(Path.Combine(folder,"upgrade-journal.json"),JsonSerializer.Serialize(new UpgradeRecord(Guid.NewGuid().ToString("N"),previous,candidate,stage,false)));
   Check(await journal.RecoverAsync(new Participant(),false,CancellationToken.None)==UpgradeStage.RolledBack && journal.Active()==previous,"Recovery from "+stage);
  }
  var coordinator=new UpgradeCoordinator(Path.Combine(root,"upgrade"));var participant=new Participant();
  await coordinator.PrepareAsync(new("1.0.0","old"),new("2.0.0","new"),participant,CancellationToken.None);
  coordinator.MarkBeforeNewFactWrite();participant.Fail=true;
  Check(await coordinator.RecoverAsync(participant,true,CancellationToken.None)==UpgradeStage.RequiresRecovery,"New facts forbid silent rollback");
  Check(await coordinator.RecoverAsync(participant,false,CancellationToken.None)==UpgradeStage.RequiresRecovery,"Recovery reentry retains unresolved candidates");
  // 固定核验期间的并发写入时序，防止恢复读取旧标记后静默丢弃新事实。
  var concurrent=new UpgradeCoordinator(Path.Combine(root,"concurrent-upgrade"));var writing=new Participant();
  await concurrent.PrepareAsync(new("1.0.0","old"),new("2.0.0","new"),writing,CancellationToken.None);
  writing.OnVerify=async ()=>{await Task.Yield();concurrent.MarkBeforeNewFactWrite();throw new InvalidDataException();};
  Check(await concurrent.RecoverAsync(writing,true,CancellationToken.None)==UpgradeStage.RequiresRecovery,"Facts written during verification forbid rollback");
  if(args.Length==0){Console.WriteLine("Live dual-plugin POC requires a published Plugins directory argument.");return;}
  var release=Path.GetFullPath(args[0]);await using var runtime=new PluginRuntime(release,Path.Combine(root,"live-storage"));await runtime.StartAsync();
  foreach(var status in runtime.Statuses) Console.WriteLine($"{status.Id}: {status.State} {status.Failure}");
  Check(runtime.Statuses.Count==2 && runtime.Statuses.All(x=>x.State==PluginState.Started),"Two real plugins started without business references");
  Check(runtime.Statuses.All(x=>Version.TryParse(x.Version,out _) && File.Exists(Path.Combine(release,x.Id,x.Version!,"plugin.json"))),"Reported versions identify the actual trusted package directories");
  Check(runtime.Pages.Count==2 && runtime.Pages.Select(x=>x.Route).Distinct().Count()==2,"Namespaced routes");
  foreach(var page in runtime.Pages)
  {
   for(var iteration=0;iteration<2;iteration++)
   {
    var view=page.Contribution.CreateView();var window=new Window {Width=1200,Height=800,Content=view};window.Show();
    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();
    Check(view.ActualWidth>0 && view.ActualHeight>0,"WPF XAML/chart layout "+page.Route);
    if(iteration==0 && FindVisual<System.Windows.Controls.TabControl>(view) is { } tabs)
    {
        // 实际切换每个页签，验证新增覆盖率和证据操作的控件资源都能创建。
        var errors=new BindingErrors();System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        try
        {
            for(var tabIndex=0;tabIndex<tabs.Items.Count;tabIndex++)
            {
                tabs.SelectedIndex=tabIndex;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.UpdateLayout();
                Check(tabs.SelectedContent is FrameworkElement {ActualHeight:>0},"Plugin tab renders "+((System.Windows.Controls.TabItem)tabs.Items[tabIndex]).Header);
            }
            Check(errors.Errors.Count==0,"All plugin tab bindings are valid");tabs.SelectedIndex=0;
            // ETF表格须保持约18行容量，整页可下滚；滚轮到表格边界后不能被内部滚动区截住。
            window.UpdateLayout();
            if(view.FindName("OverviewGrid") is System.Windows.Controls.DataGrid overview && view.FindName("PageScroll") is System.Windows.Controls.ScrollViewer pageScroll)
            {
                var capacity=(overview.ActualHeight-overview.ColumnHeaderHeight-20)/overview.RowHeight;
                Check(capacity>=15 && capacity<=20 && pageScroll.ScrollableHeight>0,"ETF table accommodates 15-20 rows and whole page scrolls");
                var tableScroll=FindVisual<System.Windows.Controls.ScrollViewer>(overview)!;
                tableScroll.ScrollToBottom();pageScroll.ScrollToTop();window.UpdateLayout();
                overview.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseWheelEvent});
                window.UpdateLayout();Check(pageScroll.VerticalOffset>0,"ETF table wheel continues scrolling the page at its boundary");
                pageScroll.ScrollToBottom();window.UpdateLayout();
                var chart=FindVisual<LiveChartsCore.SkiaSharpView.WPF.CartesianChart>(view)!;
                var chartBottom=chart.TranslatePoint(new Point(0,chart.ActualHeight),view).Y;
                Check(chartBottom<=view.ActualHeight+1 && chartBottom>0,"ETF chart becomes visible after page scroll");
                pageScroll.ScrollToTop();window.UpdateLayout();
            }
        }
        finally {System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);}
    }
    if(iteration==0)
    {
     await Task.Delay(400);window.UpdateLayout();
     var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(view);
     if(page.PluginId=="crs.demo")
     {
      var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);var blue=0;
      for(var pixel=0;pixel<pixels.Length;pixel+=4)if(pixels[pixel]>150 && pixels[pixel]>pixels[pixel+2]+40 && pixels[pixel+1]>50)blue++;
      Check(blue>100,"LiveCharts demo line rendered, not only axes");
     }
     var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
     var capture=Path.Combine(Environment.CurrentDirectory,"artifacts","plugin-ui");Directory.CreateDirectory(capture);
     using var file=File.Create(Path.Combine(capture,page.PluginId+".png"));encoder.Save(file);
    }
    window.Content=null;window.Close();
   }
  }
  runtime.SetEnabledForNextStart(runtime.Statuses[0].Id,false);
  await runtime.DisposeAsync();Check(runtime.Pages.Count==0 && runtime.Statuses.All(x=>x.State==PluginState.Stopped),"Stop/dispose and navigation cleanup");
  await using var restarted=new PluginRuntime(release,Path.Combine(root,"live-storage"));await restarted.StartAsync();Check(restarted.Statuses.Count(x=>x.State==PluginState.Disabled)==1,"Disable on next start");
  var index=JsonSerializer.Deserialize<TrustedPackage[]>(File.ReadAllText(Path.Combine(release,"trusted-plugins.json")),PackageVerifier.Json)!;
  var bad=index[0] with {Sha256=new(index[0].Sha256)};bad.Sha256["plugin.json"]=new string('0',64);
  try{PackageVerifier.Verify(release,bad,new(1,0,0));throw new Exception("Bad hash accepted");}catch(InvalidDataException){Console.WriteLine("PASS bad trusted hash rejected");}
 }
 private sealed class Participant:IPluginUpgradeParticipant
 {
  public bool Fail {get;set;}
  public Func<Task>? OnVerify {get;set;}
  public Task SnapshotAsync(ActiveGeneration previous,CancellationToken cancellationToken)=>Task.CompletedTask;
  public Task MigrateCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken)=>Task.CompletedTask;
  public Task VerifyCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken)=>OnVerify?.Invoke()??(Fail?Task.FromException(new InvalidDataException()):Task.CompletedTask);
 }
 // 使用视觉树检查真实WPF控件，不以模型或XAML字符串存在替代界面验收。
 private static T? FindVisual<T>(DependencyObject parent) where T:DependencyObject
 {
    if(parent is T match)return match;
    for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);index++)
        if(FindVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent,index)) is { } child)return child;
    return null;
 }
 private sealed class BindingErrors:System.Diagnostics.TraceListener
 {
    public List<string> Errors {get;}=[];
    public override void Write(string? message) {if(!string.IsNullOrWhiteSpace(message))Errors.Add(message);}
    public override void WriteLine(string? message)=>Write(message);
 }
}
