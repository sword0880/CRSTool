using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Background.Abstractions;

namespace CRS.Background.PluginHost;

/// <summary>信任索引位于管理员控制的宿主目录，不能由插件清单自行声明信任。</summary>
public sealed record TrustedPackage(string Id, string Version, string Directory, Dictionary<string, string> Files);
public sealed record BackgroundEntry(string Assembly, string Type);
public sealed record BackgroundManifest(string Id, string Version, string HostContract, BackgroundEntry? BackgroundEntry);
public sealed record HostedBackgroundJob(string PluginId, BackgroundJobDescriptor Descriptor, IBackgroundPluginSession Session);

/// <summary>只加载后台入口。完整包哈希验证后，私有依赖仍逐次校验，禁止链接逃逸。</summary>
public sealed class BackgroundPluginCatalog : IAsyncDisposable
{
    private readonly List<IBackgroundPluginSession> sessions = [];
    private readonly List<AssemblyLoadContext> contexts = [];
    private readonly List<HostedBackgroundJob> jobs = [];
    public IReadOnlyList<HostedBackgroundJob> Jobs => jobs;
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
            var directory = Contained(root, package.Directory);
            foreach (var file in EnumerateSafe(directory))
            {
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!package.Files.ContainsKey(relative)) throw new InvalidDataException("发行包包含未登记文件。");
            }
            foreach (var (file, hash) in package.Files) Verify(directory, file, hash);
            var manifest = JsonSerializer.Deserialize<BackgroundManifest>(File.ReadAllText(Contained(directory, "plugin.json")), Json)
                ?? throw new InvalidDataException("插件清单错误。");
            if (manifest.Id != package.Id || manifest.Version != package.Version || manifest.HostContract != "1.0")
                throw new InvalidDataException("插件版本或契约不兼容。");
            if (manifest.BackgroundEntry is not { } entry) continue; // UI-only 发行包不进入后台会话。
            var context = new VerifiedContext(directory, package.Files);
            contexts.Add(context);
            RejectDesktopDependencies(Contained(directory, entry.Assembly));
            var assembly = context.LoadFromAssemblyPath(Contained(directory, entry.Assembly));
            var instance = Activator.CreateInstance(assembly.GetType(entry.Type, throwOnError: true)!) as IBackgroundPluginEntry
                ?? throw new InvalidDataException("后台入口未实现约定接口。");
            var session = instance.CreateSession();
            sessions.Add(session);
            await session.StartAsync(token);
            var descriptors = session.DescribeJobs();
            if (descriptors.Count > 32 || descriptors.Select(x => x.JobId).Distinct().Count() != descriptors.Count)
                throw new InvalidDataException("任务重复或超过上限。");
            foreach (var descriptor in descriptors)
            {
                if (!ValidId(descriptor.JobId) || descriptor.Name.Length > 100) throw new InvalidDataException("任务标识错误。");
                jobs.Add(new(package.Id, descriptor, session));
            }
        }
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var session in sessions.AsEnumerable().Reverse())
        {
            try { await session.StopAsync(timeout.Token); }
            finally { await session.DisposeAsync(); }
        }
        sessions.Clear(); jobs.Clear();
        foreach (var context in contexts) context.Unload();
        contexts.Clear();
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
