using System.Runtime.InteropServices;
using System.Text;

namespace CRS.Infrastructure;

/// <summary>明文不得未经专项确认写入用户或系统临时目录；检查阶段只读取目录属性。</summary>
internal static class ExportDestinationPolicy
{
    internal static void Validate(string path,bool plaintext,bool temporaryLocationConfirmed)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new CrsException("请选择有效的导出位置。");
        string full;
        try {full=Path.GetFullPath(path);}
        catch(Exception error) when(error is ArgumentException or NotSupportedException or PathTooLongException)
        {throw new CrsException("导出位置格式无效，请重新选择。");}
        if(!plaintext) return;
        // 拒绝目录重解析点；短名称与设备前缀另外规范化，不能绕过临时目录判断。
        var directory=Directory.Exists(full)?full:Path.GetDirectoryName(full);
        while(directory is not null)
        {
            if(Directory.Exists(directory) && (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                throw new CrsException("明文导出不支持链接目录，请选择普通目录或使用加密导出。");
            directory=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        }
        if(temporaryLocationConfirmed) return;
        full=Normalize(full);
        var roots=new[] {Path.GetTempPath(),Environment.GetEnvironmentVariable("TEMP"),Environment.GetEnvironmentVariable("TMP"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp")};
        foreach(var root in roots.Where(r=>!string.IsNullOrWhiteSpace(r)))
        {
            var candidate=Path.TrimEndingDirectorySeparator(Normalize(Path.GetFullPath(root!)));
            if(full.Equals(candidate,StringComparison.OrdinalIgnoreCase) || full.StartsWith(candidate+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new CrsException("明文资料不能保存到临时目录，请选择可信的普通目录或加密归档导出。");
        }
    }
    private static string Normalize(string path)
    {
        if(!OperatingSystem.IsWindows()) return path;
        if(path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) path=@"\\"+path[8..];
        else if(path.StartsWith(@"\\?\",StringComparison.Ordinal)) path=path[4..];
        var suffix=new Stack<string>(); var existing=path;
        while(!Directory.Exists(existing))
        {
            var parent=Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(existing));
            if(parent is null) throw new CrsException("无法核对导出目录，请选择有效位置。");
            suffix.Push(Path.GetFileName(existing)); existing=parent;
        }
        var buffer=new StringBuilder(32768); var count=GetLongPathName(existing,buffer,(uint)buffer.Capacity);
        if(count==0 || count>=buffer.Capacity) throw new CrsException("无法核对导出目录，请选择普通目录。");
        var canonical=buffer.ToString();
        while(suffix.TryPop(out var part)) canonical=Path.Combine(canonical,part);
        return canonical;
    }
    // 只查询已存在目录的长名称，不创建文件或持久化敏感数据。
    [DllImport("kernel32.dll",EntryPoint="GetLongPathNameW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetLongPathName(string shortPath,StringBuilder longPath,uint capacity);
}
