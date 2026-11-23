using System.Buffers;
using System.Security.Cryptography;

namespace CRS.Common.Integrity;

/// <summary>有界原件稳定租约：完整SHA核验后保留禁止写入/删除的句柄，只在该租约内复用结果。</summary>
public sealed class VerifiedContentLease(int maximumFiles=8192):IDisposable,IAsyncDisposable
{
    private sealed class Entry(string hash)
    {
        public string Hash{get;}=hash;
        public long Length;
        public FileStream? File;
        public TaskCompletionSource<bool> Completion{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly int maximumFiles=maximumFiles is >=1 and <=16384?maximumFiles:throw new ArgumentOutOfRangeException(nameof(maximumFiles));
    private readonly object sync=new();
    private readonly Dictionary<string,Entry> entries=new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stopping=new();
    private readonly TaskCompletionSource pinsDrained=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pins;
    private Task? disposal;
    private bool closed;
    public bool IsActive{get{lock(sync)return !closed;}}
    public int RetainedFiles{get{lock(sync)return entries.Values.Count(x=>x.File is not null);}}
    /// <summary>短提交在所有者结束时仍保持已核文件锁；不允许在结束后签发新提交租约。</summary>
    public IDisposable PinForCommit()
    {
        lock(sync){ObjectDisposedException.ThrowIf(closed,this);if(pins>=64)throw new InvalidOperationException("原件提交租约超过上限。");pins++;return new Pin(this);}
    }
    private sealed class Pin(VerifiedContentLease owner):IDisposable
    {
        private VerifiedContentLease? current=owner;
        public void Dispose(){var held=Interlocked.Exchange(ref current,null);if(held is null)return;lock(held.sync){held.pins--;if(held.closed&&held.pins==0)held.pinsDrained.TrySetResult();}}
    }
    public Task<bool> VerifyAsync(string path,string hash,int maximumBytes,CancellationToken token,Func<CancellationToken,Task>? beforeHash=null)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("原件稳定租约需要Windows文件共享锁语义。");
        token.ThrowIfCancellationRequested();
        if(!ContentFingerprint.IsSha256Hex(hash))return Task.FromResult(false);
        if(maximumBytes<1)throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var full=Path.GetFullPath(path);Entry entry;bool added;
        lock(sync)
        {
            ObjectDisposedException.ThrowIf(closed,this);
            added=!entries.TryGetValue(full,out entry!);
            if(added)
            {
                if(entries.Count>=maximumFiles)throw new InvalidDataException("原件稳定租约超过文件数量上限。");
                entries.Add(full,entry=new(hash));
            }
            else if(!string.Equals(hash,entry.Hash,StringComparison.Ordinal))return Task.FromResult(false);
        }
        if(added)_=VerifyEntryAsync(full,entry,maximumBytes,token,beforeHash);
        return ReadResultAsync(entry,maximumBytes,token);
    }
    private static async Task<bool> ReadResultAsync(Entry entry,int maximumBytes,CancellationToken token)
        =>await entry.Completion.Task.WaitAsync(token).ConfigureAwait(false)&&entry.Length<=maximumBytes;
    private async Task VerifyEntryAsync(string path,Entry entry,int maximumBytes,CancellationToken token,Func<CancellationToken,Task>? beforeHash)
    {
        FileStream? file=null;byte[]? buffer=null;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,stopping.Token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            // Windows共享规则由内核保证；禁止写入和原子替换一直持续到提交或取消完成。
            file=new(path,FileMode.Open,FileAccess.Read,FileShare.Read,1,FileOptions.Asynchronous|FileOptions.SequentialScan);
            if(file.Length>maximumBytes){entry.Completion.TrySetResult(false);return;}
            if(beforeHash is not null)await beforeHash(cancellation.Token).ConfigureAwait(false);
            using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);buffer=ArrayPool<byte>.Shared.Rent(16384);long length=0;int read;
            while((read=await file.ReadAsync(buffer.AsMemory(),cancellation.Token).ConfigureAwait(false))>0)
            {
                length+=read;if(length>maximumBytes){entry.Completion.TrySetResult(false);return;}
                sha.AppendData(buffer,0,read);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var valid=Convert.ToHexString(sha.GetHashAndReset()).Equals(entry.Hash,StringComparison.Ordinal);
            lock(sync)
            {
                ObjectDisposedException.ThrowIf(closed,this);
                entry.Length=length;if(valid){entry.File=file;file=null;}
                entry.Completion.TrySetResult(valid);
            }
        }
        catch(OperationCanceledException){entry.Completion.TrySetCanceled(cancellation.Token);}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){entry.Completion.TrySetResult(false);}
        catch(Exception error){entry.Completion.TrySetException(error);}
        finally{file?.Dispose();if(buffer is not null)ArrayPool<byte>.Shared.Return(buffer,true);entry.Finished.TrySetResult();}
    }
    public void Dispose()=>DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        lock(sync)
        {
            if(disposal is not null)return new(disposal);
            closed=true;if(pins==0)pinsDrained.TrySetResult();
            return new(disposal=FinishAsync(entries.Values.ToArray()));
        }
    }
    private async Task FinishAsync(Entry[] active)
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(active.Select(x=>x.Finished.Task)).ConfigureAwait(false);
        try{await Task.WhenAll(active.Select(x=>x.Completion.Task)).ConfigureAwait(false);}catch(Exception){}
        await pinsDrained.Task.ConfigureAwait(false);
        lock(sync){foreach(var entry in active)entry.File?.Dispose();entries.Clear();}
        stopping.Dispose();
    }
}
