namespace CRS.IO;
/// <summary>对文件和每级父路径拒绝现存重解析点及悬空链接；不把不存在视为安全越过现存父目录。</summary>
public static class PhysicalPathGuard
{
    public static string RejectLinks(string path)
    {
        var full=Path.GetFullPath(path);
        for(var current=full;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
        {
            if(new FileInfo(current).LinkTarget is not null||new DirectoryInfo(current).LinkTarget is not null)throw new InvalidDataException("路径不可包含链接。");
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("路径不可包含重解析点。");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        return full;
    }
    public static string Child(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(':')||relative.Split('/','\\').Any(x=>x is "" or "." or ".."))throw new InvalidDataException("相对路径无效。");
        var boundary=RejectLinks(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;var full=RejectLinks(Path.Combine(boundary,relative));
        if(!full.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("路径超出指定目录。");return full;
    }
}
