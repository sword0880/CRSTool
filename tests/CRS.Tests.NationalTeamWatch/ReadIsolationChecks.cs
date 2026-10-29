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
        await using var workspace=CRS.Plugins.NationalTeamWatch.Infrastructure.NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,13);
        var hash=raw.Save(bytes);var fetch=repository!.RecordFetch(hash,"https://www.sse.com.cn/synthetic-isolation");
        repository.ImportCalendar(fetch,[("SSE",day)],default);repository.ImportShares(fetch,[new(key,day,100,"份")],default);
        var configuration=(await workspace.Etf.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        block=true;using var cancellation=new CancellationTokenSource();
        var reading=workspace.Etf.DashboardAsync(null,cancellation.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 查询停在哈希读取期间，合法写入必须先完成；这个断言不依赖磁盘快慢的平均测量。
        await workspace.Etf.SaveConfigurationAsync(key,"并发修改","test",true,"隔离验证",configuration.Revision,default).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS 查询隔离：原件核验未结束时配置写入已独立提交");
        cancellation.Cancel();
        try{await reading;throw new Exception("取消后返回了部分仪表盘");}catch(OperationCanceledException){}
        Console.WriteLine("PASS 查询隔离：取消只中断读连接，不中断写入");
        release.TrySetResult();block=false;
        var current=(await workspace.Etf.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        if(current.Name!="并发修改"||current.Revision!=configuration.Revision+1)throw new Exception("写入被查询取消回滚");
        Console.WriteLine("PASS 查询隔离：取消后新读取能看到独立提交的配置");
        var result=await workspace.Etf.DashboardAsync(null,default);
        if(result?.History.Count!=1)throw new Exception("取消影响了后续快照");
        File.WriteAllText(Path.Combine(rawPath,hash+".raw"),"damaged");
        try{await workspace.Etf.DashboardAsync(null,default);throw new Exception("新损坏被旧核验结果放行");}catch(InvalidDataException){}
        Console.WriteLine("PASS 查询隔离：每次独立快照仍重新核验原件，损坏失败关闭");
        File.WriteAllBytes(Path.Combine(rawPath,hash+".raw"),bytes);
        if((await workspace.Etf.DashboardAsync(null,default))?.History.Count!=1)throw new Exception("修复后仍不可读");
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
            var selected=await workspace.Etf.EvidenceAsync(key,day,day,default);
            if(selected.Count!=1||selected[0].TradeDate!=day.ToString("yyyy-MM-dd"))throw new Exception("日期筛选发生在读取之后");
            Console.WriteLine("PASS 查询隔离：SQL日期筛选排除范围外被锁定原件");
        }
        try
        {((IMarketReadSessions)repository).Read(current=>((IBoundedMarketEvidence)current).Evidence(key,null,null,1),default);throw new Exception("超限结果被静默截断");}
        catch(InvalidDataException){Console.WriteLine("PASS 查询隔离：SQL多读一行判定超限，拒绝返回被截断证据");}
        await IntegrityCaptureDuringWrite(Path.Combine(root,"integrity-capture"));
        await CollectionReadDuringWrite(Path.Combine(root,"activity-read"),false,false);
        await CollectionReadDuringWrite(Path.Combine(root,"activity-read-cancel"),false,true);
        await CollectionReadDuringWrite(Path.Combine(root,"share-gap-read"),true,false);
        await CollectionReadDuringWrite(Path.Combine(root,"share-gap-read-cancel"),true,true);
        await CollectionReadDuringWrite(Path.Combine(root,"activity-post-read"),false,false,true);
        await CollectionReadDuringWrite(Path.Combine(root,"activity-post-read-cancel"),false,true,true);
        GapProjectionBounds(Path.Combine(root,"gap-projection"));
        await CollectionCommitChecks.RunAsync(Path.Combine(root,"collection-commit"));
    }
    /// <summary>后台采集自身的读验也必须走独立快照；固定停在原件读取时验证写入、取消和恢复。</summary>
    private static async Task CollectionReadDuringWrite(string root,bool shareGaps,bool cancel,bool afterDownload=false)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=false;
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>Volatile.Read(ref block)?new PausedStream(File.OpenRead(path),entered,release):File.OpenRead(path));
        MarketRepository? repository=null;
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await workspace.InitializeAsync(default);var day=new DateOnly(2026,10,13);
        var row=repository!.Configuration().First();var key=new EtfKey(row.Market,row.Ticker);var source=new NoNetworkCollectionSource(afterDownload);
        if(shareGaps)
        {
            var hash=raw.Save("synthetic historical gap calendar"u8.ToArray());var fetch=repository.RecordFetch(hash,"https://www.sse.com.cn/synthetic-gap-read");
            repository.ImportCalendar(fetch,[("SSE",day)],default);
            foreach(var item in repository.Configuration().Where(x=>x.Active))repository.SetMembership(new(item.Market,item.Ticker),item.Name,item.IndexGroup,false,"隔离验证");
            row=repository.Configuration().Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
            workspace.Etf.SetOfficialSource(source);
        }
        else
        {
            if(!afterDownload)foreach(var market in new[]{"SSE","SZSE"})repository.SaveMarketActivity(market,day,new(new Uri("https://www.sse.com.cn/synthetic-activity-read"),Encoding.UTF8.GetBytes("synthetic activity "+market),market,day,new(market,day,100,100)));
            workspace.Activity.SetMarketActivitySource(source);
            if(afterDownload)workspace.SetProgressSink(p=>{if(p.Stage=="SourceParsed"&&p.Market=="SZSE")Volatile.Write(ref block,true);return Task.CompletedTask;});
        }
        using var stopping=new CancellationTokenSource();using var writingStop=new CancellationTokenSource();
        Volatile.Write(ref block,!afterDownload);
        Task reading=shareGaps?workspace.Etf.SyncRecentAsync(stopping.Token):workspace.Activity.SyncMarketActivityReportAsync(day,day,stopping.Token);
        Task? writing=null;
        var label=shareGaps?"份额历史缺口":afterDownload?"成交提交后复核":"成交已验证日期";
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            writing=workspace.Etf.SaveConfigurationAsync(key,"采集读验期间更名",row.IndexGroup,row.Active,"隔离验证",row.Revision,writingStop.Token);
            await writing.WaitAsync(TimeSpan.FromSeconds(2));
            if(reading.IsCompleted)throw new Exception("采集未保持暂停时序。");
            Console.WriteLine("PASS 查询隔离："+label+"原件仍暂停时配置写入已提交");
            if(cancel)
            {
                stopping.Cancel();try{await reading.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("取消采集读验返回了成功。");}catch(OperationCanceledException){}
                Console.WriteLine("PASS 查询隔离："+label+"取消没有返回部分成功或取消原配置写入");
            }
            else
            {Volatile.Write(ref block,false);release.TrySetResult();await reading.WaitAsync(TimeSpan.FromSeconds(5));}
        }
        finally
        {
            Volatile.Write(ref block,false);release.TrySetResult();stopping.Cancel();writingStop.Cancel();
            try{await reading;}catch(OperationCanceledException){}
            if(writing is not null)try{await writing;}catch(OperationCanceledException){}
        }
        if(source.Calls!=(afterDownload?2:0))throw new Exception("夹具来源调用次数异常。");
        var current=(await workspace.Etf.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        if(current.Name!="采集读验期间更名"||current.Revision!=row.Revision+1)throw new Exception("采集取消丢失了原配置提交。");
        if(shareGaps)await workspace.Etf.SyncRecentAsync(default);
        else
        {
            var result=await workspace.Activity.SyncMarketActivityReportAsync(day,day,default);
            if(result.ExistingSources!=2||result.RequestedSources!=0||result.SourceIssues!=0)throw new Exception("后续成交采集未复用完整已验证事实。");
        }
        if(source.Calls!=(afterDownload?2:0))throw new Exception("后续恢复不应重复请求已验证来源。");
        Console.WriteLine("PASS 查询隔离："+label+"释放后保留配置并恢复，未重复请求来源");
    }
    private static void GapProjectionBounds(string root)
    {
        var rawPath=Path.Combine(root,"raw");var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(rawPath);
        using var repository=TestMarketData.Open(data,raw);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,13);
        foreach(var row in repository.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"隔离验证");
        var fetch=repository.RecordFetch(raw.Save("synthetic gap projection"u8.ToArray()),"https://www.sse.com.cn/synthetic-gap-projection");
        repository.ImportCalendar(fetch,[(key.Market,day),(key.Market,day.AddDays(1)),(key.Market,day.AddDays(2))],default);
        repository.ImportShares(fetch,[new(key,day.AddDays(1),100,"份")],default);
        var pendingBytes="synthetic pending gap projection"u8.ToArray();var pendingHash=raw.Save(pendingBytes);
        var pendingFetch=repository.RecordFetch(pendingHash,"https://www.sse.com.cn/synthetic-pending-projection");
        repository.ImportShares(pendingFetch,[new(key,day.AddDays(1),101,"份")],default);
        // 大量已否决的范围外修订只用于固定旧整库行物化的上限反例，不是金融模型样本。
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(data,"market.db")}"))
        {
            db.Open();using var insert=db.CreateCommand();insert.CommandText="""
                WITH RECURSIVE n(i) AS(SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<10001),
                    seed AS(SELECT * FROM share_observation_revision LIMIT 1)
                INSERT INTO share_observation_revision
                SELECT 'scale-gap-'||n.i,s.market,s.ticker,'2020-01-02',s.total_shares,s.original_value,s.original_unit,
                    s.fetch_id,n.i,NULL,s.first_seen,'Rejected',NULL FROM n CROSS JOIN seed s
                """;insert.ExecuteNonQuery();
        }
        var bounded=false;try{((IMarketReadSessions)repository).Read(r=>r.Evidence(),default);}catch(InvalidDataException){bounded=true;}
        if(!bounded)throw new Exception("大证据集夹具未触及整库查询上限。");
        var now=CRS.Timing.BeijingClock.Now;
        List<ShareGap> Read()=>((IMarketReadSessions)repository).Read(r=>r.DueShareGaps(day,day.AddDays(2),now),default);
        var gaps=Read();
        if(gaps.Count!=2||gaps.Any(x=>x.Date==day.AddDays(1))||!gaps.SequenceEqual(repository.DueShareGaps(day,day.AddDays(2),now)))throw new Exception("有界日期投影改变了待审排除或读写等价结果。");
        Console.WriteLine("PASS 查询隔离：超过一万范围外修订不阻断缺口读取，待审排除及读写结果一致");
        File.WriteAllBytes(Path.Combine(rawPath,pendingHash+".raw"),"damaged synthetic pending"u8.ToArray());repository.ReconcileEvidenceIntegrity();
        if(Read().Count!=3)throw new Exception("损坏待审原件仍被用来排除缺口。");
        File.WriteAllBytes(Path.Combine(rawPath,pendingHash+".raw"),pendingBytes);repository.ReconcileEvidenceIntegrity();
        if(Read().Count!=2)throw new Exception("同哈希修复后待审日期未恢复。");
        Console.WriteLine("PASS 查询隔离：缺口投影保留损坏隔离、重新读验和同哈希修复语义");
    }
    private sealed class NoNetworkCollectionSource(bool supplyActivity):IOfficialShareSource,IMarketActivitySource
    {
        public int Calls;
        public Task<SourceDay> FetchAsync(DateOnly date,CancellationToken token)
        {Interlocked.Increment(ref Calls);throw new Exception("不应请求份额来源。");}
        public Task<MarketActivitySource> FetchAsync(string market,DateOnly date,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);if(!supplyActivity)throw new Exception("不应请求成交来源。");
            return Task.FromResult(new MarketActivitySource(new("https://www.sse.com.cn/synthetic-downloaded-activity"),Encoding.UTF8.GetBytes("synthetic downloaded "+market),market,date,new(market,date,100,100)));
        }
    }
    private static async Task IntegrityCaptureDuringWrite(string root)
    {
        var writerEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWriter=new ManualResetEventSlim();var block=false;
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>
        {if(Volatile.Read(ref block))verificationEntered.TrySetResult();return File.OpenRead(path);});
        MarketRepository? repository=null;
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw,()=>
        {if(Volatile.Read(ref block)){writerEntered.TrySetResult();if(!releaseWriter.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException("隔离写门槛未释放。");}}),raw);
        await workspace.InitializeAsync(default);
        var hash=raw.Save("synthetic integrity capture"u8.ToArray());repository!.RecordFetch(hash,"https://www.sse.com.cn/synthetic-integrity-capture");
        var row=(await workspace.Etf.ConfigurationAsync(default)).First();var key=new EtfKey(row.Market,row.Ticker);
        Volatile.Write(ref block,true);
        var write=workspace.Etf.SaveConfigurationAsync(key,"独立写入","synthetic",row.Active,"测试",row.Revision,default);
        using var cancellation=new CancellationTokenSource();Task? integrity=null;
        try
        {
            await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            integrity=workspace.Evidence.ReconcileEvidenceIntegrityAsync(cancellation.Token);
            await verificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Console.WriteLine("PASS 查询隔离：写门槛仍被占用时巡检已独立读取依赖并开始核验原件");
            cancellation.Cancel();try{await integrity;throw new Exception("巡检等待写入时忽略取消。");}catch(OperationCanceledException){}
            Console.WriteLine("PASS 查询隔离：巡检提交前取消不取消占用写门槛的原写入");
        }
        finally{Volatile.Write(ref block,false);releaseWriter.Set();if(integrity is not null)try{await integrity;}catch(OperationCanceledException){}await write;}
        var after=(await workspace.Etf.ConfigurationAsync(default)).Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        if(after.Name!="独立写入"||after.Revision!=row.Revision+1)throw new Exception("原写入未保留。");
        await workspace.Evidence.ReconcileEvidenceIntegrityAsync(default);
        Console.WriteLine("PASS 查询隔离：原配置写入保留且后续完整性巡检正常完成");
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
