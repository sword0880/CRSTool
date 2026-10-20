using System.Security.Cryptography;

namespace CRS.Infrastructure;

/// <summary>单次限量读取的来源副本；解析器与来源摘要共用同一份私有字节。</summary>
public sealed class FrozenInput
{
    private const int MaxBytes=25*1024*1024;
    private readonly byte[] content;
    public string Name {get;}
    public int Length=>content.Length;
    public string Sha256 {get;}
    private FrozenInput(string path,byte[] bytes)
    {
        content=bytes; Name=Path.GetFileName(path);
        Sha256=Convert.ToHexStringLower(SHA256.HashData(content));
    }
    internal static FrozenInput Read(string path,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        // 读取期间拒绝写入与替换；随后关闭原件，后续解析不再依赖原路径。
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <=0 or >MaxBytes) throw new CrsException("源文件必须非空且不能超过 25 MB。");
        var bytes=new byte[(int)file.Length]; var offset=0;
        while(offset<bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var count=file.Read(bytes,offset,Math.Min(65536,bytes.Length-offset));
            if(count==0) throw new CrsException("源文件读取不完整，请重新选择原件。");
            offset+=count;
        }
        cancellation.ThrowIfCancellationRequested();
        if(file.ReadByte()!=-1) throw new CrsException("源文件在读取期间发生变化，请重试。");
        return new(path,bytes);
    }
    // 不公开底层数组，避免某个解析器修改其他用途所依赖的冻结内容。
    public Stream OpenRead()=>new MemoryStream(content,0,content.Length,false,false);
    internal byte[] CopyBytes()=>content.ToArray();
    internal static FrozenInput FromBytes(string name,byte[] raw)
    {if(raw.Length is <=0 or >MaxBytes)throw new CrsException("源文件大小无效。");return new(name,raw);}
}
