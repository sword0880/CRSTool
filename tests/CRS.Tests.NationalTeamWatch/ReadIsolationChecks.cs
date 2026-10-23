using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>阻塞真实原件读取来固定并发时序，证明采集写入不等待慢查询，取消也不跨连接传播。</summary>
internal static class ReadIsolationChecks
{
    public static async Task RunAsync(string root)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=false;
        var rawPath=Path.Combine(root,"raw");var bytes=Encoding.UTF8.GetBytes("isolated read evidence");
        var raw=new RawEvidenceStore(rawPath,openRead:path=>block?new PausedStream(File.OpenRead(path),entered,release):File.OpenRead(path));
        MarketRepository? repository=null;
        await using var workspace=new EtfWorkspace(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,13);
        var hash=raw.Save(bytes);var fetch=repository!.RecordFetch(hash,"https://www.sse.com.cn/synthetic-isolation");
        repository.ImportCalendar(fetch,[("SSE",day)],default);repository.ImportShares(fetch,[new(key,day,100,"份")],default);
        var configuration=(await workspace.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        block=true;using var cancellation=new CancellationTokenSource();
        var reading=workspace.DashboardAsync(null,cancellation.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 查询停在哈希读取期间，合法写入必须先完成；这个断言不依赖磁盘快慢的平均测量。
        await workspace.SaveConfigurationAsync(key,"并发修改","test",true,"隔离验证",configuration.Revision,default).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS 查询隔离：原件核验未结束时配置写入已独立提交");
        cancellation.Cancel();
        try{await reading;throw new Exception("取消后返回了部分仪表盘");}catch(OperationCanceledException){}
        Console.WriteLine("PASS 查询隔离：取消只中断读连接，不中断写入");
        release.TrySetResult();block=false;
        var current=(await workspace.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        if(current.Name!="并发修改"||current.Revision!=configuration.Revision+1)throw new Exception("写入被查询取消回滚");
        Console.WriteLine("PASS 查询隔离：取消后新读取能看到独立提交的配置");
        var result=await workspace.DashboardAsync(null,default);
        if(result?.History.Count!=1)throw new Exception("取消影响了后续快照");
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"damaged");
        try{await workspace.DashboardAsync(null,default);throw new Exception("新损坏被旧核验结果放行");}catch(InvalidDataException){}
        Console.WriteLine("PASS 查询隔离：每次独立快照仍重新核验原件，损坏失败关闭");
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),bytes);
        if((await workspace.DashboardAsync(null,default))?.History.Count!=1)throw new Exception("修复后仍不可读");
        Console.WriteLine("PASS 查询隔离：原件修复后重新核验并恢复读取");
        try{((IMarketReadSessions)repository).Read(reader=>{reader.RecordSyncCursor(day);return true;},default);throw new Exception("只读会话允许写入");}
        catch(InvalidOperationException){Console.WriteLine("PASS 查询隔离：只读会话禁止写入和初始化数据库");}
        var later=day.AddDays(1);var laterHash=raw.Save("later isolated evidence"u8.ToArray());
        var laterFetch=repository.RecordFetch(laterHash,"https://www.sse.com.cn/synthetic-later");
        repository.ImportCalendar(laterFetch,[("SSE",later)],default);
        repository.ImportShares(laterFetch,[new(key,later,101,"份")],default);
        // 独占锁定范围外的原件，证明日期筛选发生在SQL及原件核验之前。
        using(var outside=new FileStream(Path.Combine(rawPath,laterHash+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            var selected=await workspace.EvidenceAsync(key,day,day,default);
            if(selected.Count!=1||selected[0].TradeDate!=day.ToString("yyyy-MM-dd"))throw new Exception("日期筛选发生在读取之后");
            Console.WriteLine("PASS 查询隔离：SQL日期筛选排除范围外被锁定原件");
        }
        try
        {((IMarketReadSessions)repository).Read(current=>((IBoundedMarketEvidence)current).Evidence(key,null,null,1),default);throw new Exception("超限结果被静默截断");}
        catch(InvalidDataException){Console.WriteLine("PASS 查询隔离：SQL多读一行判定超限，拒绝返回被截断证据");}
    }
    private sealed class PausedStream(Stream inner,TaskCompletionSource entered,TaskCompletionSource release):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>inner.CanSeek;public override bool CanWrite=>false;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {entered.TrySetResult();await release.Task.WaitAsync(token);return await inner.ReadAsync(buffer,token);}
        public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);
        public override void Flush()=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
