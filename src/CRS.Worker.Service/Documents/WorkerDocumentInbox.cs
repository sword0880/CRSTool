using CRS.IO.Staging;
namespace CRS.Worker.Service;

/// <summary>把公共暂存机制装配到插件私有目录；公开请求只携带标识，不接收路径。</summary>
public sealed class WorkerDocumentInbox(string dataRoot)
{
    internal StagedDocumentStore For(string plugin)
    {
        if(plugin.Length is <1 or >80||!char.IsAsciiLetterLower(plugin[0])||plugin.EndsWith('.')||plugin.Any(c=>!char.IsAsciiLetterLower(c)&&!char.IsAsciiDigit(c)&&c is not '.' and not '-'))throw new InvalidDataException("插件标识无效。");
        return new(Path.Combine(dataRoot,"plugins",plugin,"document-inbox"));
    }
}
