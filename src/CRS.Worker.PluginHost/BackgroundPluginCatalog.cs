using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Worker.Abstractions;

namespace CRS.Worker.PluginHost;

/// <summary>信任索引位于管理员控制的宿主目录，不能由插件清单自行声明信任。</summary>
public sealed record TrustedPackage(string Id, string Version, string Directory, Dictionary<string, string> Files);
public sealed record BackgroundEntry(string Assembly, string Type);
public sealed record BackgroundManifest(string Id, string Version, string HostContract, BackgroundEntry? BackgroundEntry)
{
    public BackgroundEntry? StorageMigrationEntry {get;init;}
}
public sealed record HostedBackgroundJob(string PluginId, BackgroundJobDescriptor Descriptor, IBackgroundPluginSession Session, int MaximumConcurrency = 1)
{
    public BackgroundExecutionGroup? ExecutionGroup {get;init;}
}

/// <summary>只加载后台入口。完整包哈希验证后，私有依赖仍逐次校验，禁止链接逃逸。</summary>
public sealed partial class BackgroundPluginCatalog(TimeSpan? lifecycleTimeout = null, Action? onUnresponsive = null,
    Func<string,IBackgroundHostContext>? hostContext = null, Action<string,string,string>? lifecycleDiagnostic = null,
    Func<TrustedPackage,IBackgroundPluginSession>? processSessions=null) : IAsyncDisposable
{
    // 保留既有四参数构造入口；进程监督是宿主新增装配能力，不改变旧测试/宿主的调用签名。
    public BackgroundPluginCatalog(TimeSpan? lifecycleTimeout,Action? onUnresponsive,
        Func<string,IBackgroundHostContext>? hostContext,Action<string,string,string>? lifecycleDiagnostic)
        :this(lifecycleTimeout,onUnresponsive,hostContext,lifecycleDiagnostic,null){}
    private readonly List<(string Plugin,IBackgroundPluginSession Session)> sessions = [];
    private PluginLifecycleTimeoutException? terminalFault;
    public bool RequiresProcessRestart=>Volatile.Read(ref terminalFault) is not null;
    private void EnsureResponsive(){if(Volatile.Read(ref terminalFault) is {} failure)throw failure;}
    private readonly List<AssemblyLoadContext> contexts = [];
    private readonly List<HostedBackgroundJob> jobs = [];
    // 查询能力独立于任务描述列表，支持零 Job 插件。
    private readonly Dictionary<string,IBackgroundQuerySession> querySessions = new(StringComparer.Ordinal);
    public bool SupportsQueries(string plugin)=>querySessions.ContainsKey(plugin);
    private readonly List<BackgroundPluginStatus> statuses = [];
    private readonly List<string> expectedPlugins = [];
    public IReadOnlyList<string> ExpectedPlugins=>expectedPlugins.ToArray();
    public IReadOnlyList<HostedBackgroundJob> Jobs => jobs;
    public IReadOnlyList<BackgroundPluginStatus> Statuses => statuses.Select(status=>
        sessions.FirstOrDefault(x=>x.Plugin==status.PluginId).Session is IsolatedPluginSession {FailureCode:{} code}
            ?new BackgroundPluginStatus(status.PluginId,"Failed",code):status).ToArray();
    public bool CanDispatch(string plugin)=>sessions.FirstOrDefault(x=>x.Plugin==plugin).Session is not IsolatedPluginSession isolated||isolated.CanRun;
    /// <summary>查询仅分派到已成功启动的插件，并沿用有界生命周期等待策略。</summary>
    public async Task<string> QueryAsync(string plugin,string payload,CancellationToken token)
    {
        EnsureResponsive();
        if(!querySessions.TryGetValue(plugin,out var session))throw new ArgumentException("插件不支持查询。");
        var phase="Query";
        var watch=System.Diagnostics.Stopwatch.StartNew();string result="";
        try{await InvokeAsync(plugin,"Query",async cancellation=>
        {
            // 诊断扩展也是插件代码，必须在同一有界生命周期内调用。
            var operation=session is IBackgroundQueryDiagnosticsSession diagnostic?diagnostic.QueryOperation(payload):null;
            if(operation is not null&&Regex.IsMatch(operation,"^[a-z][a-z0-9-]{0,39}$",RegexOptions.CultureInvariant))phase="Query."+operation;
            cancellation.ThrowIfCancellationRequested();result=await session.QueryAsync(payload,cancellation);
        },token,()=>phase);}
        finally{if(watch.ElapsedMilliseconds>=1000)ReportLifecycle(plugin,phase,"ElapsedMs="+watch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));}
        if(System.Text.Encoding.UTF8.GetByteCount(result)>48000)throw new InvalidDataException("业务查询响应超限。");
        return result;
    }
    /// <summary>宿主校验调度声明失败时隔离整个插件，避免其它插件随之退出。</summary>
    public void Quarantine(string pluginId, string errorCode)
    {
        jobs.RemoveAll(x => x.PluginId == pluginId);
        querySessions.Remove(pluginId);
        statuses.RemoveAll(x => x.PluginId == pluginId);
        statuses.Add(new(pluginId, "Failed", errorCode));
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task LoadAsync(string root,string trustFile,CancellationToken token)=>LoadCoreAsync(root,trustFile,null,token);
    public Task LoadSingleAsync(string root,string trustFile,string pluginId,CancellationToken token)=>LoadCoreAsync(root,trustFile,pluginId,token);
    private async Task LoadCoreAsync(string root, string trustFile,string? selectedPlugin, CancellationToken token)
    {
        EnsureResponsive();
        var packages = await ReadPackagesAsync(trustFile,token);
        foreach (var package in packages)
        {
            if(selectedPlugin is not null&&package.Id!=selectedPlugin)continue;
            IBackgroundPluginSession? session = null;
            VerifiedContext? context = null;
            try
            {
            var manifest = VerifyManifest(root,package,out var directory);
            if (manifest.BackgroundEntry is not { } entry) continue; // UI-only 发行包不进入后台会话。
            expectedPlugins.Add(package.Id);
            RejectDesktopDependencies(Contained(directory, entry.Assembly));
            if(processSessions is not null)session=processSessions(package);
            else
            {
            context = new VerifiedContext(directory, package.Files);
            contexts.Add(context);
            var assembly = context.LoadFromAssemblyPath(Contained(directory, entry.Assembly));
            await InvokeAsync(package.Id,"CreateSession",_ =>
            {
                var instance = Activator.CreateInstance(assembly.GetType(entry.Type, throwOnError: true)!) as IBackgroundPluginEntry
                    ?? throw new InvalidDataException("后台入口未实现约定接口。");
                session = instance is IContextualBackgroundPluginEntry contextual
                    ? contextual.CreateSession(hostContext?.Invoke(package.Id) ?? throw new InvalidDataException("缺少后台存储上下文。"))
                    : instance.CreateSession(); return Task.CompletedTask;
            }, token);
            }
            if (session is null) throw new InvalidDataException("后台入口未建立会话。");
            sessions.Add((package.Id,session));
            await InvokeAsync(package.Id,"Start",cancellation => session.StartAsync(cancellation).AsTask(), token);
            IReadOnlyList<BackgroundJobDescriptor> descriptors = [];
            await InvokeAsync(package.Id,"DescribeJobs",_ => { descriptors = session.DescribeJobs(); return Task.CompletedTask; }, token);
            var maximumConcurrency = 1;
            await InvokeAsync(package.Id,"Concurrency",_ => { maximumConcurrency = (session as IConcurrentBackgroundSession)?.MaximumConcurrentExecutions ?? 1; return Task.CompletedTask; }, token);
            if (maximumConcurrency is < 1 or > 32) throw new InvalidDataException("插件并发声明超出上限。");
            if (descriptors.Count > 32 || descriptors.Select(x => x.JobId).Distinct().Count() != descriptors.Count)
                throw new InvalidDataException("任务重复或超过上限。");
            var groups=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach (var descriptor in descriptors)
            {
                if (!ValidId(descriptor.JobId) || descriptor.Name.Length > 100) throw new InvalidDataException("任务标识错误。");
                BackgroundExecutionGroup? group=null;
                if(session is IGroupedBackgroundSession grouped)
                {
                    await InvokeAsync(package.Id,"ExecutionGroup",_=>{group=grouped.ExecutionGroup(descriptor.JobId);return Task.CompletedTask;},token);
                    if(group is null||!ValidId(group.Id)||group.MaximumConcurrency<1||group.MaximumConcurrency>maximumConcurrency
                        ||groups.TryGetValue(group.Id,out var prior)&&prior!=group.MaximumConcurrency)
                        throw new InvalidDataException("任务执行组声明无效。");
                    groups[group.Id]=group.MaximumConcurrency;
                }
                jobs.Add(new(package.Id, descriptor, session, maximumConcurrency){ExecutionGroup=group});
            }
            if(session is IBackgroundQuerySession querySession)querySessions.Add(package.Id,querySession);
            statuses.Add(new(package.Id, "Started"));
            }
            catch (PluginLifecycleTimeoutException) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // 单个包损坏或初始化失败只隔离该插件，保持其它可信任务可用。
                jobs.RemoveAll(x => x.PluginId == package.Id);
                querySessions.Remove(package.Id);
                if (session is not null)
                {
                    try { await CleanupAsync(package.Id,session, token); }
                    catch (PluginLifecycleTimeoutException) { throw; }
                    catch (Exception) { }
                    sessions.RemoveAll(x=>ReferenceEquals(x.Session,session));
                }
                if (context is not null) { contexts.Remove(context); context.Unload(); }
                statuses.Add(new(package.Id, "Failed", error.GetType().Name));
            }
        }
    }

    private void ReportLifecycle(string plugin,string phase,string code)
    {
        try{lifecycleDiagnostic?.Invoke(plugin,phase,code);}catch(Exception){ /* 诊断失败不能跳过退出保护。 */ }
    }
    private async Task InvokeAsync(string plugin,string phase,Func<CancellationToken, Task> work, CancellationToken token,Func<string>? diagnosticPhase=null)
    {
        EnsureResponsive();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(lifecycleTimeout ?? TimeSpan.FromSeconds(15));
        var task = Task.Run(() => work(deadline.Token), CancellationToken.None);
        try
        {
            try { await task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!task.IsCompleted)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(sessions.Any(x=>x.Plugin==plugin&&x.Session is IsolatedPluginSession)?5:1)); }
                catch (TimeoutException) when (!task.IsCompleted)
                {
                    _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    var failure=new PluginLifecycleTimeoutException(plugin,phase);
                    Interlocked.CompareExchange(ref terminalFault,failure,null);
                    ReportLifecycle(plugin,diagnosticPhase?.Invoke()??phase,"CancellationGraceExceeded");
                    try{onUnresponsive?.Invoke();}catch(Exception){ /* 回调失败仍保留终止状态。 */ }
                    throw failure;
                }
            }
            // 插件迟到返回也不能把已取消/超时的调用冒充成功。
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch(PluginLifecycleTimeoutException){throw;}
        catch(Exception error)
        {
            ReportLifecycle(plugin,diagnosticPhase?.Invoke()??phase,error is OperationCanceledException
                ?token.IsCancellationRequested?"CallerCanceled":"TimeoutCanceled":error.GetType().Name);
            throw;
        }
    }
    private async Task CleanupAsync(string plugin,IBackgroundPluginSession session, CancellationToken token)
    {
        Exception? stopFailure = null;
        try { await InvokeAsync(plugin,"Stop",cancellation => session.StopAsync(cancellation).AsTask(), token); }
        catch (PluginLifecycleTimeoutException) { throw; }
        catch (Exception error) { stopFailure = error; }
        await InvokeAsync(plugin,"Dispose",_ => session.DisposeAsync().AsTask(), token);
        if (stopFailure is not null) throw new InvalidOperationException("后台插件停止失败，已尝试资源释放。", stopFailure);
    }

    private static bool ValidId(string id) => Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,79}$", RegexOptions.CultureInvariant);
    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发行包不可包含链接。");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var file in EnumerateSafe(path)) yield return file;
            else yield return path;
        }
    }
    private static void RejectDesktopDependencies(string path)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var reference in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(reference).Name);
            if (name.StartsWith("Presentation", StringComparison.Ordinal) || name.StartsWith("System.Windows", StringComparison.Ordinal)
                || name is "WindowsBase" or "CRS.Security" or "CRS.Infrastructure" or "CRS.Domain" or "CRS.Application" or "Quartz")
                throw new InvalidDataException("后台插件引用桌面、私人税务或独立调度实现。");
        }
    }
    private static string Contained(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("发行包路径不合法。");
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发行包路径越界。");
        for (var current = target; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("发行路径不可包含重解析点。");
        }
        return target;
    }
    private static void Verify(string directory, string relative, string hash)
    {
        using var stream = File.OpenRead(Contained(directory, relative));
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("后台发行包完整性验证失败。");
    }
    public async ValueTask DisposeAsync()
    {
        EnsureResponsive(); // 不合作插件仍在运行时不得Stop/Dispose/Unload，必须退出进程。
        using var timeout = new CancellationTokenSource(lifecycleTimeout ?? TimeSpan.FromSeconds(15));
        var failures = new List<Exception>();
        foreach (var (plugin,session) in sessions.AsEnumerable().Reverse())
        {
            try { await CleanupAsync(plugin,session, timeout.Token); }
            catch (PluginLifecycleTimeoutException) { throw; }
            catch (Exception error) { failures.Add(error); }
        }
        sessions.Clear(); jobs.Clear();querySessions.Clear();
        foreach (var context in contexts) context.Unload();
        contexts.Clear();
        if (failures.Count != 0) throw new AggregateException("后台插件释放存在异常。", failures);
    }
    private sealed class VerifiedContext(string directory, Dictionary<string, string> files) : AssemblyLoadContext(isCollectible: true)
    {
        /// <summary>原生依赖也必须来自已核验包；不从任意当前目录解析插件提供的库。</summary>
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var relative=$"runtimes/win-x64/native/{name}.dll";
            if(!files.TryGetValue(relative,out var hash))return IntPtr.Zero;
            Verify(directory,relative,hash);return LoadUnmanagedDllFromPath(Contained(directory,relative));
        }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IBackgroundPluginEntry).Assembly.GetName().Name) return typeof(IBackgroundPluginEntry).Assembly;
            if (name.Name is null || name.Name.StartsWith("Presentation", StringComparison.Ordinal) || name.Name.StartsWith("System.Windows", StringComparison.Ordinal)
                || name.Name.StartsWith("CRS.Security", StringComparison.Ordinal) || name.Name.StartsWith("CRS.Infrastructure", StringComparison.Ordinal))
                throw new FileLoadException("后台插件不允许加载桌面或私人数据程序集。");
            var file = name.Name + ".dll";
            if (!files.TryGetValue(file, out var hash))
            {
                // 平台程序集只能由平台加载；其它依赖必须包含于受信包。
                if (name.Name.StartsWith("System.", StringComparison.Ordinal) || name.Name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) || name.Name == "netstandard") return null;
                throw new FileLoadException("后台依赖未列入受信文件索引。");
            }
            Verify(directory, file, hash);
            RejectDesktopDependencies(Contained(directory, file));
            return LoadFromAssemblyPath(Contained(directory, file));
        }
    }
}

/// <summary>插件仍在运行时禁止假装已卸载；实际服务收到此信号会有界终止进程。</summary>
public sealed class PluginLifecycleTimeoutException(string pluginId="unknown",string phase="Unknown") : Exception("Plugin lifecycle requires process restart.")
{
    public string PluginId{get;}=pluginId;
    public string Phase{get;}=phase;
}
