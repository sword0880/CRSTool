using System.IO;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>SHA-256内容寻址存证；保存、核验和导出均保留原始字节。</summary>
public sealed class RawEvidenceStore(string root,Action? beforeWrite=null,Func<string,Stream>? openRead=null) : IRawEvidenceStore
{
    /// <summary>只导出已经复核哈希的字节；先写同目录临时文件，取消或失败不覆盖已有目标。</summary>
    public async Task ExportVerifiedAsync(string hash,string destination,IReadOnlyList<string> protectedDirectories,CancellationToken cancellationToken)
    {
        if(hash.Length!=64 || !hash.All(Uri.IsHexDigit))throw new InvalidDataException("证据哈希格式无效。");
        var target=Path.GetFullPath(destination);
        foreach(var directory in protectedDirectories)
        {
            var boundary=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(target.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("不能把导出文件写入插件的数据库、证据、日志或备份目录。");
        }
        // 拒绝重解析目录，避免符号链接将普通导出路径转向插件存储。
        for(var directory=new DirectoryInfo(Path.GetDirectoryName(target)!);directory is not null;directory=directory.Parent)
            if(directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("导出目录不能包含符号链接或重解析点。");
        var bytes=await ReadImportFileAsync(Path.Combine(root,hash+".raw"),cancellationToken).ConfigureAwait(false);
        if(Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("原始证据损坏，拒绝导出为已核验文件。");
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,true))
            {await output.WriteAsync(bytes,cancellationToken).ConfigureAwait(false);output.Flush(true);}
            cancellationToken.ThrowIfCancellationRequested();File.Move(temporary,target,true);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public async Task<byte[]> ReadImportFileAsync(string path,CancellationToken cancellationToken)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,true);
        if(input.Length>8*1024*1024)throw new InvalidDataException("每个文件不能超过8MB。");
        using var content=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await input.ReadAsync(buffer,cancellationToken).ConfigureAwait(false))>0)
        {if(content.Length+read>8*1024*1024)throw new InvalidDataException("每个文件不能超过8MB。");await content.WriteAsync(buffer.AsMemory(0,read),cancellationToken).ConfigureAwait(false);}
        return content.ToArray();
    }
    public string Save(byte[] content)
    {
        // 即便稍后失败或去重，也先通过宿主写入门槛；升级时保守保留候选数据。
        beforeWrite?.Invoke();
        Directory.CreateDirectory(root);
        var hash = Convert.ToHexString(SHA256.HashData(content)); var target = Path.Combine(root, hash + ".raw");
        if (File.Exists(target)) { if (!Verify(hash)) throw new InvalidDataException("Evidence integrity failure."); return hash; }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(content); output.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return hash;
    }
    public bool Verify(string hash)=>VerifyAsync(hash,CancellationToken.None).GetAwaiter().GetResult();
    private async Task<bool> VerifyAsync(string hash,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return false;
        var path = Path.Combine(root, hash + ".raw");
        try
        {
            // 同一句柄逐块异步读取；取消能中断正在等待的文件读取，而不只在文件之间检查。
            await using var stream=openRead?.Invoke(path)??new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,16384,FileOptions.Asynchronous|FileOptions.SequentialScan);
            if(stream.Length>8*1024*1024)return false;
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[16384];var total=0;
            while(true)
            {
                var read=await stream.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();if(read==0)break;
                total+=read;if(total>8*1024*1024)return false;
                digest.AppendData(buffer,0,read);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(digest.GetHashAndReset())==hash;
        }
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    /// <summary>仅本次操作共享结果；最多四个只读句柄并行，下一次查询仍重新校验全部原件。</summary>
    public IReadOnlyDictionary<string,bool> VerifyMany(IEnumerable<string> hashes,CancellationToken token)
    {
        var results=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>(StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        Parallel.ForEachAsync(hashes.Distinct(StringComparer.Ordinal),
            new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=token},
            async(hash,cancellation)=>results[hash]=await VerifyAsync(hash,cancellation).ConfigureAwait(false))
            .GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        return results;
    }
}
