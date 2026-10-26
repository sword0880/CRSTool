using System.IO;
using System.Security.Cryptography;
using CRS.Plugin.Common.Evidence;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>SHA-256内容寻址存证；保存、核验和导出均保留原始字节。</summary>
public sealed class RawEvidenceStore(string root,Action? beforeWrite=null,Func<string,Stream>? openRead=null) : IRawEvidenceStore,IRawEvidenceVerifier
{
    private readonly int verificationParallelism=8;
    private readonly RawEvidenceReadAdmission verificationReads=new(8);
    private readonly AsyncLocal<bool> queryVerification=new();
    public RawEvidenceReadActivity VerificationActivity=>verificationReads.Activity;
    /// <summary>只读金融快照优先核验；作用域随逻辑查询流动，不缓存原件，不改变知识时间或完整性结论。</summary>
    public IDisposable PrioritizeQueryVerification()
    {var previous=queryVerification.Value;queryVerification.Value=true;return new QueryScope(()=>queryVerification.Value=previous);}
    private sealed class QueryScope(Action restore):IDisposable
    {private Action? current=restore;public void Dispose()=>Interlocked.Exchange(ref current,null)?.Invoke();}
    /// <summary>仅调整有界读取并发，不缓存核验结论；保留原三参数构造签名，供真实只读A/B测量及隔离取消检查。</summary>
    public RawEvidenceStore(string root,Action? beforeWrite,Func<string,Stream>? openRead,int verificationParallelism):this(root,beforeWrite,openRead)
    {
        if(verificationParallelism is <1 or >16)throw new ArgumentOutOfRangeException(nameof(verificationParallelism));
        this.verificationParallelism=verificationParallelism;
        verificationReads=new(verificationParallelism);
    }
    /// <summary>只导出已经复核哈希的字节；先写同目录临时文件，取消或失败不覆盖已有目标。</summary>
    public async Task ExportVerifiedAsync(string hash,string destination,IReadOnlyList<string> protectedDirectories,CancellationToken cancellationToken)
    {
        if(!ContentFingerprint.IsSha256Hex(hash))throw new InvalidDataException("证据哈希格式无效。");
        var target=Path.GetFullPath(destination);
        foreach(var directory in protectedDirectories)
        {
            var boundary=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(target.StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("不能把导出文件写入插件的数据库、证据、日志或备份目录。");
        }
        // 拒绝重解析目录，避免符号链接将普通导出路径转向插件存储。
        for(var directory=new DirectoryInfo(Path.GetDirectoryName(target)!);directory is not null;directory=directory.Parent)
            if(directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("导出目录不能包含符号链接或重解析点。");
        var bytes=await ReadBoundedAsync(Path.Combine(root,hash+".raw"),OfficialEquityDocuments.MaximumEvidenceBytes(hash),cancellationToken).ConfigureAwait(false);
        if(!ContentFingerprint.Matches(hash,bytes))throw new InvalidDataException("原始证据损坏，拒绝导出为已核验文件。");
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
        =>await ReadBoundedAsync(path,8*1024*1024,cancellationToken).ConfigureAwait(false);
    // 普通导入仍限8MiB；只有已绑定摘要的正式原件读验/导出可使用较大上限，不能靠文件名批准任意正文。
    private async Task<byte[]> ReadBoundedAsync(string path,int maximumBytes,CancellationToken cancellationToken)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,true);
        if(input.Length>maximumBytes)throw new InvalidDataException("原件超过允许的文件大小。");
        using var content=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await input.ReadAsync(buffer,cancellationToken).ConfigureAwait(false))>0)
        {if(content.Length+read>maximumBytes)throw new InvalidDataException("原件超过允许的文件大小。");await content.WriteAsync(buffer.AsMemory(0,read),cancellationToken).ConfigureAwait(false);}
        return content.ToArray();
    }
    public string Save(byte[] content)
    {
        // 即便稍后失败或去重，也先通过宿主写入门槛；升级时保守保留候选数据。
        beforeWrite?.Invoke();
        Directory.CreateDirectory(root);
        var hash = ContentFingerprint.Sha256(content); var target = Path.Combine(root, hash + ".raw");
        // 大小及身份不符在写临时原件前拒绝，不能留下之后必然无法核验的金融证据。
        if(content.Length>OfficialEquityDocuments.MaximumEvidenceBytes(hash))throw new InvalidDataException("原件超过允许的文件大小。");
        if (File.Exists(target)) { if (!Verify(hash)) throw new InvalidDataException("Evidence integrity failure."); return hash; }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(content); output.Flush(true); }
            try{File.Move(temporary, target);}
            catch(IOException) when(File.Exists(target))
            {
                // 并行来源可能取得相同原件；只接受另一执行已原子保存且哈希正确的文件。
                if(!Verify(hash))throw new InvalidDataException("Evidence integrity failure.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return hash;
    }
    public bool Verify(string hash)=>VerifyAsync(hash,CancellationToken.None).GetAwaiter().GetResult();
    private async Task<bool> VerifyAsync(string hash,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ContentFingerprint.IsSha256Hex(hash)) return false;
        var path = Path.Combine(root, hash + ".raw");
        try
        {
            // 每个批次仍有界并行，同一库的所有批次还共享额度；后台巡检不能把句柄数乘到24并与查询争抢磁盘。
            using var lease=await verificationReads.EnterAsync(queryVerification.Value,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // 同一句柄逐块异步读取；取消能中断正在等待的文件读取，而不只在文件之间检查。
            await using var stream=openRead?.Invoke(path)??new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,16384,FileOptions.Asynchronous|FileOptions.SequentialScan);
            var maximumBytes=OfficialEquityDocuments.MaximumEvidenceBytes(hash);
            if(stream.Length>maximumBytes)return false;
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[16384];var total=0;
            while(true)
            {
                var read=await stream.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();if(read==0)break;
                total+=read;if(total>maximumBytes)return false;
                digest.AppendData(buffer,0,read);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(digest.GetHashAndReset())==hash;
        }
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
    }
    /// <summary>仅本次操作共享结果；默认最多八个只读句柄并行，下一次查询仍重新校验全部原件。</summary>
    public IReadOnlyDictionary<string,bool> VerifyMany(IEnumerable<string> hashes,CancellationToken token)
    {
        var results=new System.Collections.Concurrent.ConcurrentDictionary<string,bool>(StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        Parallel.ForEachAsync(hashes.Distinct(StringComparer.Ordinal),
            new ParallelOptions{MaxDegreeOfParallelism=verificationParallelism,CancellationToken=token},
            async(hash,cancellation)=>results[hash]=await VerifyAsync(hash,cancellation).ConfigureAwait(false))
            .GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        return results;
    }
}
