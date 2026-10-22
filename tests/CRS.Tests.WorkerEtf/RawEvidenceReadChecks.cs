using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class RawEvidenceReadChecks
{
    internal static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.RawBatch",Guid.NewGuid().ToString("N"));
        var store=new RawEvidenceStore(root);var hashes=Enumerable.Range(0,64).Select(i=>store.Save(Encoding.UTF8.GetBytes("synthetic evidence "+i))).ToArray();
        var missing=new string('0',64);var all=hashes.Append(missing).Append("invalid").Concat(hashes).ToArray();
        void Check(bool value,string text){if(!value)throw new InvalidOperationException(text);Console.WriteLine("PASS 批量原件校验："+text);}
        var results=store.VerifyMany(all,default);
        Check(results.Count==66&&hashes.All(x=>results[x])&&!results[missing]&&!results["invalid"],"去重后完整验证原件、缺失及非法标识");
        File.WriteAllText(Path.Combine(root,hashes[0]+".raw"),"changed");
        Check(!store.VerifyMany(all,default)[hashes[0]],"下一次操作重新读取并发现原件被修改");
        File.Delete(Path.Combine(root,hashes[1]+".raw"));
        Check(!store.VerifyMany(all,default)[hashes[1]],"下一次操作发现已删除原件");
        using var stop=new CancellationTokenSource();stop.Cancel();
        try{store.VerifyMany(all,stop.Token);throw new InvalidOperationException("取消未生效");}
        catch(OperationCanceledException){Check(true,"取消不返回部分校验结果");}
        CancelDuringRead(root,hashes);
    }
    private static void CancelDuringRead(string root,string[] hashes)
    {
        using var entered=new ManualResetEventSlim();using var stop=new CancellationTokenSource();
        var opened=0;var disposed=0;
        var slow=new RawEvidenceStore(root,openRead:_=>
        {Interlocked.Increment(ref opened);return new WaitingStream(entered,()=>Interlocked.Increment(ref disposed));});
        var pending=Task.Run(()=>slow.VerifyMany(hashes,stop.Token));
        if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("没有进入正在等待的原件读取。");
        var elapsed=Stopwatch.StartNew();stop.Cancel();
        try{pending.WaitAsync(TimeSpan.FromMilliseconds(800)).GetAwaiter().GetResult();throw new Exception("取消后返回了部分校验结果。");}
        catch(OperationCanceledException){}
        if(opened>4||opened!=disposed)throw new Exception("取消后原件句柄泄漏或超出并发上限。");
        Console.WriteLine($"PASS 正在等待的原件读取取消：{elapsed.ElapsedMilliseconds}ms，全部{opened}个句柄释放，不返回部分结果。");
        var failing=new RawEvidenceStore(root,openRead:_=>throw new IOException("synthetic read failure"));
        try{failing.VerifyMany(hashes,default);throw new Exception("I/O错误被当作完整结果。");}
        catch(IOException){Console.WriteLine("PASS 原件I/O失败向上传播，不返回部分校验结果。");}
    }
    private sealed class WaitingStream(ManualResetEventSlim entered,Action closed):Stream
    {
        public override bool CanRead=>true;public override bool CanWrite=>false;public override bool CanSeek=>false;
        public override long Length=>1024;public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {entered.Set();await Task.Delay(Timeout.Infinite,token);return 0;}
        protected override void Dispose(bool disposing){if(disposing)closed();base.Dispose(disposing);}
        public override int Read(byte[] buffer,int offset,int count)=>throw new Exception("不应使用不可取消的同步读取。");
        public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    internal static void Profile(string root)
    {
        var paths=Directory.GetFiles(root,"*.raw");var hashes=paths.Select(Path.GetFileNameWithoutExtension).Select(x=>x!).ToArray();
        var timer=Stopwatch.StartNew();
        var previous=paths.ToDictionary(path=>Path.GetFileNameWithoutExtension(path)!,path=>
        {
            if(!File.Exists(path)||new FileInfo(path).Length>8*1024*1024)return false;
            using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input))==Path.GetFileNameWithoutExtension(path);
        });
        var before=timer.ElapsedMilliseconds;timer.Restart();
        var current=new RawEvidenceStore(root).VerifyMany(hashes,default);var after=timer.ElapsedMilliseconds;
        if(previous.Any(x=>current[x.Key!]!=x.Value))throw new InvalidOperationException("原件校验结果改变。");
        Console.WriteLine(JsonSerializer.Serialize(new{Files=paths.Length,BeforeMs=before,AfterMs=after,Identical=true,Invalid=current.Count(x=>!x.Value)}));
    }
}
