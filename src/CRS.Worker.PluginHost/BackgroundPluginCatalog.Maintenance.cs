using System.Text.Json;
using CRS.Worker.Abstractions;

namespace CRS.Worker.PluginHost;

public sealed partial class BackgroundPluginCatalog
{
    internal static async Task<TrustedPackage[]> ReadPackagesAsync(string trustFile,CancellationToken token)
    {
        CRS.IO.PhysicalPathGuard.RejectLinks(trustFile);
        if(!File.Exists(trustFile)||new FileInfo(trustFile).Length>1024*1024)throw new InvalidDataException("后台信任索引缺失或超限。");
        var packages=JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(trustFile,token),Json)??throw new InvalidDataException("信任索引格式错误。");
        if(packages.Length>32)throw new InvalidDataException("后台插件数量超过上限。");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var package in packages)
            if(package is null||!ValidId(package.Id)||!ids.Add(package.Id)||package.Files is null||package.Files.Count is <1 or >256)throw new InvalidDataException("插件标识重复或不合法。");
        return packages;
    }
    private static BackgroundManifest VerifyManifest(string root,TrustedPackage package,out string directory)
    {
        directory=Contained(root,package.Directory);
        foreach(var file in EnumerateSafe(directory))
            if(!package.Files.ContainsKey(Path.GetRelativePath(directory,file).Replace('\\','/')))throw new InvalidDataException("发行包包含未登记文件。");
        foreach(var (file,hash) in package.Files)Verify(directory,file,hash);
        var manifest=JsonSerializer.Deserialize<BackgroundManifest>(File.ReadAllText(Contained(directory,"plugin.json")),Json)??throw new InvalidDataException("插件清单错误。");
        if(manifest.Id!=package.Id||manifest.Version!=package.Version||manifest.HostContract!="2.0")throw new InvalidDataException("插件版本或契约不兼容。");
        return manifest;
    }
    public static async Task<TrustedPackage> VerifyStorageMigrationPackageAsync(string root,string trustFile,string pluginId,CancellationToken token)
    {
        var packages=await ReadPackagesAsync(trustFile,token);
        var package=packages.SingleOrDefault(x=>x.Id==pluginId)??throw new InvalidDataException("维护插件未登记。");
        var manifest=VerifyManifest(root,package,out var directory);
        var entry=manifest.StorageMigrationEntry??throw new InvalidDataException("插件没有独立存储维护入口。");
        RejectDesktopDependencies(Contained(directory,entry.Assembly));
        return package;
    }
    /// <summary>只在独立维护Runner中执行；复用正常包核验与依赖加载，不进入普通会话及其目录租约。</summary>
    public static async Task<BackgroundStorageMigrationResult> MigrateAsync(string root,string trustFile,string packageHash,
        BackgroundStorageMigrationContext migration,CancellationToken token)
    {
        var packages=await ReadPackagesAsync(trustFile,token);
        var package=packages.SingleOrDefault(x=>x.Id==migration.PluginId)??throw new InvalidDataException("维护插件未登记。");
        if(RunnerChannel.PackageHash(package)!=packageHash)throw new InvalidDataException("维护插件发行绑定不符。");
        var manifest=VerifyManifest(root,package,out var directory);
        var entry=manifest.StorageMigrationEntry??throw new InvalidDataException("插件没有独立存储维护入口。");
        var assemblyPath=Contained(directory,entry.Assembly);RejectDesktopDependencies(assemblyPath);
        var context=new VerifiedContext(directory,package.Files);
        try
        {
            var assembly=context.LoadFromAssemblyPath(assemblyPath);
            var instance=Activator.CreateInstance(assembly.GetType(entry.Type,throwOnError:true)!) as IBackgroundStorageMigrationEntry
                ??throw new InvalidDataException("维护入口未实现存储协议。");
            token.ThrowIfCancellationRequested();
            var result=await instance.MigrateAsync(migration,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(result.MigrationId!=migration.MigrationId||result.OperationId!=migration.OperationId||result.State is not ("Completed" or "AlreadyCompleted")
                ||result.ConvertedDatabases is <0 or >64||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(result.EvidenceSha256))throw new InvalidDataException("维护结果身份或状态不符。");
            return result;
        }
        finally{context.Unload();}
    }
}
