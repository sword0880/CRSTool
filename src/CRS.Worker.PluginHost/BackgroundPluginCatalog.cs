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
public sealed record BackgroundManifest(string Id, string Version, string HostContract, BackgroundEntry? BackgroundEntry);
public sealed record HostedBackgroundJob(string PluginId, BackgroundJobDescriptor Descriptor, IBackgroundPluginSession Session);

/// <summary>只加载后台入口。完整包哈希验证后，私有依赖仍逐次校验，禁止链接逃逸。</summary>
public sealed class BackgroundPluginCatalog(TimeSpan? lifecycleTimeout = null, Action? onUnresponsive = null) : IAsyncDisposable
{
    private readonly List<IBackgroundPluginSession> sessions = [];
    private readonly List<AssemblyLoadContext> contexts = [];
    private readonly List<HostedBackgroundJob> jobs = [];
    private readonly List<BackgroundPluginStatus> statuses = [];
    public IReadOnlyList<HostedBackgroundJob> Jobs => jobs;
    public IReadOnlyList<BackgroundPluginStatus> Statuses => statuses;
    /// <summary>宿主校验调度声明失败时隔离整个插件，避免其它插件随之退出。</summary>
    public void Quarantine(string pluginId, string errorCode)
    {
        jobs.RemoveAll(x => x.PluginId == pluginId);
        statuses.RemoveAll(x => x.PluginId == pluginId);
        statuses.Add(new(pluginId, "Failed", errorCode));
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task LoadAsync(string root, string trustFile, CancellationToken token)
    {
        if (!File.Exists(trustFile)) throw new InvalidDataException("缺少宿主信任索引，拒绝启动后台任务。");
        if (new FileInfo(trustFile).Length > 1024 * 1024) throw new InvalidDataException("信任索引过大。");
        var packages = JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(trustFile, token), Json)
            ?? throw new InvalidDataException("信任索引格式错误。");
        if (packages.Length > 32) throw new InvalidDataException("后台插件数量超过上限。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (!ValidId(package.Id) || !ids.Add(package.Id) || package.Files.Count is < 1 or > 256)
                throw new InvalidDataException("插件标识重复或不合法。");
            IBackgroundPluginSession? session = null;
            VerifiedContext? context = null;
            try
            {
            var directory = Contained(root, package.Directory);
            foreach (var file in EnumerateSafe(directory))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!package.Files.ContainsKey(relative)) throw new InvalidDataException("发行包包含未登记文件。");
            }
            foreach (var (file, hash) in package.Files) Verify(directory, file, hash);
            var manifest = JsonSerializer.Deserialize<BackgroundManifest>(File.ReadAllText(Contained(directory, "plugin.json")), Json)
                ?? throw new InvalidDataException("插件清单错误。");
            if (manifest.Id != package.Id || manifest.Version != package.Version || manifest.HostContract != "2.0")
                throw new InvalidDataException("插件版本或契约不兼容。");
            if (manifest.BackgroundEntry is not { } entry) continue; // UI-only 发行包不进入后台会话。
            context = new VerifiedContext(directory, package.Files);
            contexts.Add(context);
            RejectDesktopDependencies(Contained(directory, entry.Assembly));
            var assembly = context.LoadFromAssemblyPath(Contained(directory, entry.Assembly));
            await InvokeAsync(_ =>
            {
                var instance = Activator.CreateInstance(assembly.GetType(entry.Type, throwOnError: true)!) as IBackgroundPluginEntry
                    ?? throw new InvalidDataException("后台入口未实现约定接口。");
                session = instance.CreateSession(); return Task.CompletedTask;
            }, token);
            if (session is null) throw new InvalidDataException("后台入口未建立会话。");
            sessions.Add(session);
            await InvokeAsync(cancellation => session.StartAsync(cancellation).AsTask(), token);
            IReadOnlyList<BackgroundJobDescriptor> descriptors = [];
            await InvokeAsync(_ => { descriptors = session.DescribeJobs(); return Task.CompletedTask; }, token);
            if (descriptors.Count > 32 || descriptors.Select(x => x.JobId).Distinct().Count() != descriptors.Count)
                throw new InvalidDataException("任务重复或超过上限。");
            foreach (var descriptor in descriptors)
            {
                if (!ValidId(descriptor.JobId) || descriptor.Name.Length > 100) throw new InvalidDataException("任务标识错误。");
                jobs.Add(new(package.Id, descriptor, session));
            }
            statuses.Add(new(package.Id, "Started"));
            }
            catch (PluginLifecycleTimeoutException) { throw; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // 单个包损坏或初始化失败只隔离该插件，保持其它可信任务可用。
                jobs.RemoveAll(x => x.PluginId == package.Id);
                if (session is not null)
                {
                    try { await CleanupAsync(session, token); }
                    catch (PluginLifecycleTimeoutException) { throw; }
                    catch (Exception) { }
                    sessions.Remove(session);
                }
                if (context is not null) { contexts.Remove(context); context.Unload(); }
                statuses.Add(new(package.Id, "Failed", error.GetType().Name));
            }
        }
    }

    private async Task InvokeAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(lifecycleTimeout ?? TimeSpan.FromSeconds(15));
        var task = Task.Run(() => work(deadline.Token), CancellationToken.None);
        try { await task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            // 等待超时不代表执行已终止；先给协作取消短暂收尾，再要求宿主终止进程。
            try { await task.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                onUnresponsive?.Invoke(); throw new PluginLifecycleTimeoutException();
            }
        }
    }
    private async Task CleanupAsync(IBackgroundPluginSession session, CancellationToken token)
    {
        Exception? stopFailure = null;
        try { await InvokeAsync(cancellation => session.StopAsync(cancellation).AsTask(), token); }
        catch (PluginLifecycleTimeoutException) { throw; }
        catch (Exception error) { stopFailure = error; }
        await InvokeAsync(_ => session.DisposeAsync().AsTask(), token);
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
        using var timeout = new CancellationTokenSource(lifecycleTimeout ?? TimeSpan.FromSeconds(15));
        var failures = new List<Exception>();
        foreach (var session in sessions.AsEnumerable().Reverse())
        {
            try { await CleanupAsync(session, timeout.Token); }
            catch (PluginLifecycleTimeoutException) { throw; }
            catch (Exception error) { failures.Add(error); }
        }
        sessions.Clear(); jobs.Clear();
        foreach (var context in contexts) context.Unload();
        contexts.Clear();
        if (failures.Count != 0) throw new AggregateException("后台插件释放存在异常。", failures);
    }
    private sealed class VerifiedContext(string directory, Dictionary<string, string> files) : AssemblyLoadContext(isCollectible: true)
    {
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
public sealed class PluginLifecycleTimeoutException : Exception;
