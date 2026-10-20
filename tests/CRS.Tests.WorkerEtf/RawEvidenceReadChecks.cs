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
