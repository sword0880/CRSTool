using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using CRS.Plugin.Abstractions;

namespace CRS.PluginHost;

/// <summary>已核验包的默认上下文加载范围，供会话和独立升级进程共用。</summary>
internal sealed class VerifiedPluginLoad : IDisposable
{
    private readonly string directory;
    private readonly TrustedPackage package;
    private readonly AssemblyDependencyResolver resolver;
    public ICrsPluginEntry Entry {get;}
    public VerifiedPluginLoad(string releaseRoot,TrustedPackage package,PluginManifest manifest)
    {
        this.package=package;directory=PackageVerifier.Resolve(releaseRoot,package.Directory);
        var entryPath=PackageVerifier.Resolve(directory,manifest.EntryAssembly);resolver=new(entryPath);
        var identity=AssemblyName.GetAssemblyName(entryPath);
        var existing=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>x.GetName().Name==identity.Name);
        if(existing is not null)
        {
            // 默认上下文不能在进程内替换同名业务代码；只有完全相同的字节才可复用。
            if(existing.IsDynamic || existing.GetName().FullName!=identity.FullName)throw new InvalidDataException("业务程序集已加载，请在独立进程执行升级。");
            using var oldFile=File.OpenRead(existing.Location);using var newFile=File.OpenRead(entryPath);
            if(!SHA256.HashData(oldFile).SequenceEqual(SHA256.HashData(newFile)))throw new InvalidDataException("同名业务代码不同，请重新启动或使用独立升级进程。");
        }
        AssemblyLoadContext.Default.Resolving+=Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll+=ResolveNative;
        try
        {
            var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(entryPath);
            Entry=Activator.CreateInstance(assembly.GetType(manifest.EntryType,true)!) as ICrsPluginEntry??throw new InvalidDataException("插件入口契约不匹配。");
        }
        catch {Dispose();throw;}
    }
    private void VerifyResolved(string path)
    {
        var relative=Path.GetRelativePath(directory,path).Replace('\\','/');
        if(!package.Sha256.ContainsKey(relative))throw new InvalidDataException("依赖不属于可信包文件清单。");
    }
    private Assembly? Resolve(AssemblyLoadContext context,AssemblyName name)
    {
        var path=resolver.ResolveAssemblyToPath(name);if(path is null)return null;
        var identity=AssemblyName.GetAssemblyName(path);
        // 编译引用允许使用集中锁定的较新补丁；已加载的实际版本冲突仍在包核验阶段拒绝。
        if(identity.Version<name.Version || !(identity.GetPublicKeyToken()??[]).SequenceEqual(name.GetPublicKeyToken()??[]))throw new InvalidDataException("插件依赖版本不兼容。");
        VerifyResolved(path);return context.LoadFromAssemblyPath(path);
    }
    private IntPtr ResolveNative(Assembly assembly,string name)
    {
        if(!assembly.Location.StartsWith(directory+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return IntPtr.Zero;
        var path=resolver.ResolveUnmanagedDllToPath(name);if(path is null)return IntPtr.Zero;
        VerifyResolved(path);return NativeLibrary.Load(path);
    }
    public void Dispose()
    {
        AssemblyLoadContext.Default.Resolving-=Resolve;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll-=ResolveNative;
    }
}
