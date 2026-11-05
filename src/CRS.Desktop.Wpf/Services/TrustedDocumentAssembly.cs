using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace CRS.DesktopClient.Services;

/// <summary>主程序内嵌可信构建指纹；验证后固定只读文件至进程退出，避免校验后被替换。</summary>
public static partial class TrustedDocumentAssembly
{
    private static readonly object gate=new();
    private static Assembly? verified;
    private static FileStream? pinned;
    public static void Load(string? path=null)
    {
        lock(gate)
        {
            if(verified is not null)
            {
                if(pinned?.SafeFileHandle.IsClosed!=false)throw new InvalidDataException("文档组件保护句柄已失效。");
                return;
            }
            // 已被其他入口抢先加载的组件不能仅凭磁盘上另一份文件的哈希获得信任。
            if(AssemblyLoadContext.Default.Assemblies.Any(a=>a.GetName().Name=="CRS.Documents"))
                throw new InvalidDataException("文档组件未经验证已被加载，请使用完整的正式程序包。");
            path=Path.GetFullPath(path??Path.Combine(AppContext.BaseDirectory,"CRS.Documents.dll"));
            // 拒绝链接路径，避免父目录重定向改变 CLR 随后打开的实际文件。
            for(FileSystemInfo? item=new FileInfo(path);item is not null;item=item is FileInfo f?f.Directory:((DirectoryInfo)item).Parent)
                if((item.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("文档组件路径不支持链接。");
            var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                if(source.Length is <=0 or >32*1024*1024) throw new InvalidDataException("文档组件完整性校验失败。");
                var actual=SHA256.HashData(source);
                if(!CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(ExpectedSha256)))
                    throw new InvalidDataException("文档组件完整性校验失败，请重新安装完整程序包。");
                // 在 Windows 保持只读且不共享删除／写入的句柄，整个会话不允许替换组件。
                // 使用路径加载保留 Location，使现有插件包核验仍能复核公共依赖字节。
                var loaded=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                if(loaded.GetName().Name!="CRS.Documents" || !Path.GetFullPath(loaded.Location).Equals(path,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("文档组件身份无效。");
                pinned=source;verified=loaded;
            }
            catch {source.Dispose();throw;}
        }
    }
}
