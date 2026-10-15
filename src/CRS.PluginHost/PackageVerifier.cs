using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CRS.PluginHost;

/// <summary>插件清单仅声明兼容性与文件；可信性必须来自宿主发行索引。</summary>
public sealed record PluginManifest(string Id, string DisplayName, string Version, string ContractApi,
    string MinHostVersion, int SchemaVersion, string EntryAssembly, string EntryType, string[] RequestedCapabilities, string[] Files,
    string RuntimeTarget="net10.0-windows10.0.19041.0",string MinOsVersion="10.0.19041.0",string? MaxHostVersion=null);
// 此索引属于经过验证的宿主发行包，不能相信插件自行提供的哈希声明。
public sealed record TrustedPackage(string Directory, Dictionary<string, string> Sha256);
/// <summary>在执行插件前核验路径、完整文件清单、哈希、版本及共享程序集冲突。</summary>
public static class PackageVerifier
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Invalid package path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path escapes root.");
        for (var item = new DirectoryInfo(Path.GetDirectoryName(full)!); item is not null && item.FullName.Length >= fullRoot.Length - 1; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Reparse point in package.");
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Reparse file in package.");
        return full;
    }
    public static PluginManifest Verify(string releaseRoot, TrustedPackage trusted, Version hostVersion)
    {
        var directory = Resolve(releaseRoot, trusted.Directory);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var (relative, expected) in trusted.Sha256)
        {
            if (!files.Add(relative.Replace('\\', '/'))) throw new InvalidDataException("Duplicate package file.");
            var path = Resolve(directory, relative);
            size += new FileInfo(path).Length;
            if (size > 200 * 1024 * 1024) throw new InvalidDataException("Package too large.");
            using var input = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release hash mismatch.");
        }
        // 默认加载上下文可能自动解析依赖，因此执行前就必须拒绝未登记的原生或托管文件。
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            if (!files.Contains(Path.GetRelativePath(directory, path).Replace('\\', '/'))) throw new InvalidDataException("Unlisted package file.");
        if (!files.Contains("plugin.json")) throw new InvalidDataException("Manifest not bound to host release.");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Resolve(directory, "plugin.json")), Json) ?? throw new InvalidDataException("Missing manifest.");
        if (!Regex.IsMatch(manifest.Id, "^[a-z0-9]+(?:[.-][a-z0-9]+)+$") || manifest.ContractApi != "1.0" || manifest.SchemaVersion < 1
            || !Version.TryParse(manifest.Version, out _) || !Version.TryParse(manifest.MinHostVersion, out var minimum) || minimum > hostVersion)
            throw new InvalidDataException("Incompatible plugin contract/version.");
        if (manifest.RuntimeTarget!="net10.0-windows10.0.19041.0" || !Version.TryParse(manifest.MinOsVersion,out var minimumOs) || Environment.OSVersion.Version<minimumOs
            || (manifest.MaxHostVersion is not null && (!Version.TryParse(manifest.MaxHostVersion,out var maximum) || hostVersion>maximum))) throw new InvalidDataException("Incompatible runtime/OS/host.");
        if(trusted.Directory.Replace('\\','/')!=$"{manifest.Id}/{manifest.Version}") throw new InvalidDataException("Package directory identity mismatch.");
        string[] capabilities = ["navigation", "jobs", "http-public", "storage-private", "logging", "schedules"];
        if (manifest.RequestedCapabilities.Any(x => !capabilities.Contains(x))) throw new InvalidDataException("Unsupported capability.");
        if (!files.Contains(manifest.EntryAssembly) || manifest.Files.Any(x => !files.Contains(x))) throw new InvalidDataException("Manifest files not trusted.");
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.dll"))
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(path); } catch (BadImageFormatException) { continue; }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == name.Name);
            if (loaded is not null && loaded.GetName().Version != name.Version) throw new InvalidDataException("Shared assembly version conflict.");
        }
        return manifest;
    }
}
