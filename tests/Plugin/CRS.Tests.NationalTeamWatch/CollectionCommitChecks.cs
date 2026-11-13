using System.Text;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>固定份额提交前的真实原件读取，验证配置并发、取消及已提交事实边界。</summary>
internal static class CollectionCommitChecks
{
    public static async Task RunAsync(string root)
    {
        var legacy=System.Text.Json.JsonSerializer.Deserialize<SyncReport>("""{"QueriedDays":1,"SourceIssues":0,"ReceivedRows":1}""")!;
        if(legacy.ImportPreparationMilliseconds is not null||legacy.ImportCommitHeldMilliseconds is not null||legacy.ImportPreparationAttempts is not null)throw new Exception("旧载荷未记录的耗时被补为零。");
        Console.WriteLine("PASS 份额提交：旧载荷未记录的准备、持有耗时及次数保持未知");
        var rawProxy=System.Reflection.DispatchProxy.Create<IRawEvidenceStore,UnsupportedRawProxy>();var opened=false;var rejected=false;
        try{NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>{opened=true;throw new Exception("不应打开资料库。");},rawProxy);}catch(NotSupportedException){rejected=true;}
        if(!rejected||opened)throw new Exception("缺少原件稳定能力仍初始化了后台资料库。");
        Console.WriteLine("PASS 份额提交：缺少原件核验/稳定租约能力在打开资料库之前拒绝");
        await PausedImport(Path.Combine(root,"resume"),false);
        await PausedImport(Path.Combine(root,"cancel"),true);
        await ContinuousChanges(Path.Combine(root,"continuous-change"),true);
        await ContinuousChanges(Path.Combine(root,"unrelated-change"),false);
        await ConcurrentEvidence(Path.Combine(root,"concurrent-evidence"),false);
        await ConcurrentEvidence(Path.Combine(root,"concurrent-damage"),true);
        await PostFetchAdmission(Path.Combine(root,"post-fetch-admission"));
        VersionCapabilities(Path.Combine(root,"versions"));
        await RawCaptureCancellation(Path.Combine(root,"capture-cancel"));
    }
    public class UnsupportedRawProxy:System.Reflection.DispatchProxy
    {protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? arguments)=>throw new Exception("不应调用缺少能力的原件代理。");}
    private static async Task PausedImport(string root,bool cancel)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=false;
        var seedBytes="synthetic collection seed"u8.ToArray();var seedHash=CRS.Common.Integrity.ContentFingerprint.Sha256(seedBytes);
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>Volatile.Read(ref block)&&Path.GetFileName(path)==seedHash+".raw"
            ?new Paused(File.OpenRead(path),entered,release):File.OpenRead(path));
        var incomingPath=Path.Combine(root,"raw",CRS.Common.Integrity.ContentFingerprint.Sha256("synthetic collection incoming"u8.ToArray())+".raw");
        var normalized=Encoding.UTF8.GetBytes("market,ticker,trade_date,total_shares,unit\nSSE,510300,2026-10-14,101,万份");
        var normalizedPath=Path.Combine(root,"raw",CRS.Common.Integrity.ContentFingerprint.Sha256(normalized)+".raw");var commitProbes=0;
        MarketRepository? repository=null;
        await using var app=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw,()=>
        {
            if(!File.Exists(incomingPath)||!File.Exists(normalizedPath))return;
            foreach(var path in new[]{incomingPath,normalizedPath})
            {
                var denied=false;try{using var write=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}catch(IOException){denied=true;}
                if(!denied)throw new Exception("提交体中原件或规范化字节未保持稳定锁。");
            }
            commitProbes++;
        }),raw);
        await app.InitializeAsync(default);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,14);
        foreach(var row in repository!.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"隔离验证");
        var fetch=repository.RecordFetch(raw.Save(seedBytes),"https://www.sse.com.cn/synthetic-collection-seed");
        repository.ImportCalendar(fetch,[(key.Market,day.AddDays(-1)),(key.Market,day)],default);
        repository.ImportShares(fetch,[new(key,day.AddDays(-1),100,"万份")],default);
        var initial=repository.Configuration().Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
        var additional=repository.Configuration().First(x=>x.Market!=key.Market||x.Ticker!=key.Ticker);
        var source=new Source(key,day);app.Etf.SetOfficialSource(source);
        using var stopping=new CancellationTokenSource();using var writingStop=new CancellationTokenSource();
        Volatile.Write(ref block,true);var importing=app.Etf.SyncAsync(day,day,stopping.Token);Task? writing=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            async Task Save()
            {
                await app.Etf.SaveConfigurationAsync(key,"份额读验期间更名",initial.IndexGroup,true,"隔离验证",initial.Revision,writingStop.Token);
                await app.Etf.SaveConfigurationAsync(new(additional.Market,additional.Ticker),additional.Name,additional.IndexGroup,true,"隔离验证",additional.Revision,writingStop.Token);
            }
            writing=Save();
            await writing.WaitAsync(TimeSpan.FromSeconds(2));
            if(importing.IsCompleted)throw new Exception("导入未保持暂停时序。");
            Console.WriteLine("PASS 份额提交：历史原件读验仍暂停时配置写入已提交");
            if(cancel)
            {
                stopping.Cancel();try{await importing.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("取消返回了部分成功。");}catch(OperationCanceledException){}
            }
            else
            {
                Volatile.Write(ref block,false);release.TrySetResult();var report=await importing.WaitAsync(TimeSpan.FromSeconds(10));
                if(report.ImportPreparationAttempts is not >=2||report.ImportCommitHeldMilliseconds is not >=0)throw new Exception("版本变化未重新准备或耗时回执缺失。");
                Console.WriteLine("PASS 份额提交：配置变化重新准备后提交，区分准备与实际持有写入名额耗时");
            }
        }
        finally
        {
            Volatile.Write(ref block,false);release.TrySetResult();stopping.Cancel();writingStop.Cancel();
            try{await importing;}catch(OperationCanceledException){}
            if(writing is not null)try{await writing;}catch(OperationCanceledException){}
        }
        var current=await app.Etf.ConfigurationAsync(default);
        if(current.Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker).Name!="份额读验期间更名")throw new Exception("导入取消丢失了并发配置提交。");
        var points=await app.Etf.PointsAsync(key,default);
        if(points.Count!=(cancel?1:2)||source.Calls!=1)throw new Exception("取消前写入了新事实，或重算重复请求了官网。");
        var attempt=(await app.Etf.CollectionAttemptsAsync(key,day,day,default)).First();
        if(attempt.State!=(cancel?"Interrupted":"Accepted"))throw new Exception("采集终态与实际提交不一致。");
        if(!cancel)
        {
            if(commitProbes==0||!File.ReadAllBytes(normalizedPath).SequenceEqual(normalized))throw new Exception("提交未验证原件锁或规范化字节发生变化。");
            Console.WriteLine("PASS 份额提交：进入写入体后原件与规范化字节仍禁止修改，保存字节与原口径一致");
        }
        foreach(var file in Directory.GetFiles(Path.Combine(root,"raw"),"*.raw"))using(var probe=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        Console.WriteLine("PASS 份额提交："+(cancel?"取消保留旧事实及并发配置，尝试中断且原件锁释放":"恢复后只请求一次来源，事实与采集终态一致且原件锁释放"));
    }
    private static async Task ContinuousChanges(string root,bool poolChanges)
    {
        var seedBytes="synthetic changing collection seed"u8.ToArray();var seedHash=CRS.Common.Integrity.ContentFingerprint.Sha256(seedBytes);var change=false;var changes=0;
        EtfKey? changedPoolKey=null;var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>
        {
            if(Volatile.Read(ref change)&&Path.GetFileName(path)==seedHash+".raw")
            {
                using var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(data,"market.db")};Pooling=False");db.Open();
                using var command=db.CreateCommand();command.CommandText=poolChanges?"UPDATE watchlist_membership SET active=1-active WHERE market=$m AND ticker=$t":"UPDATE nt_alert_configuration SET revision=revision+1";
                if(poolChanges){command.Parameters.AddWithValue("$m",changedPoolKey!.Market);command.Parameters.AddWithValue("$t",changedPoolKey.Ticker);}command.ExecuteNonQuery();Interlocked.Increment(ref changes);
            }
            return File.OpenRead(path);
        });
        MarketRepository? repository=null;await using var app=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(data,raw),raw);
        await app.InitializeAsync(default);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,14);
        foreach(var row in repository!.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"隔离验证");
        var additional=repository.Configuration().First(x=>x.Market!=key.Market||x.Ticker!=key.Ticker);changedPoolKey=new(additional.Market,additional.Ticker);
        var fetch=repository.RecordFetch(raw.Save(seedBytes),"https://www.sse.com.cn/synthetic-changing-seed");
        repository.ImportCalendar(fetch,[(key.Market,day.AddDays(-1)),(key.Market,day)],default);repository.ImportShares(fetch,[new(key,day.AddDays(-1),100,"万份")],default);
        var source=new Source(key,day);app.Etf.SetOfficialSource(source);Volatile.Write(ref change,true);var deferred=false;
        SyncReport? report=null;
        try{report=await app.Etf.SyncAsync(day,day,default);}catch(CRS.Common.Concurrency.WorkAdmissionException error) when(error.Reason=="InputsChanged"){deferred=true;}
        finally{Volatile.Write(ref change,false);}
        var attempt=(await app.Etf.CollectionAttemptsAsync(key,day,day,default)).First();
        if(!poolChanges)
        {
            if(deferred||changes!=1||source.Calls!=1||report?.ImportPreparationAttempts!=1||attempt.State!="Accepted"||(await app.Etf.PointsAsync(key,default)).Count!=2)throw new Exception("不相关资料变更仍使份额准备失效。");
            Console.WriteLine("PASS 份额提交：不相关资料变更不丢弃已核原件，提交体仍读取当前事实");return;
        }
        if(!deferred||changes!=3||source.Calls!=1||attempt.State!="Interrupted"||attempt.Reason!="InputsChanged"||(await app.Etf.PointsAsync(key,default)).Count!=1)
            throw new Exception("连续变更未在三次后退避，或错误归为解析失败/写入新事实。");
        Console.WriteLine("PASS 份额提交：其它连接持续变更只准备三次，明确退避并保留原事实和来源请求身份");
        await app.Etf.SyncAsync(day,day,default);
        var resumedAttempts=await app.Etf.CollectionAttemptsAsync(key,day,day,default);
        if((await app.Etf.PointsAsync(key,default)).Count!=2||resumedAttempts.First().State!="Accepted"||resumedAttempts.Single(x=>x.Id==attempt.Id).State!="Interrupted")throw new Exception("资料稳定后无法恢复提交，或原中断尝试被覆盖。");
        Console.WriteLine("PASS 份额提交：资料稳定后正常恢复，原中断尝试保留");
    }
    private sealed class ForeignVersion:IEtfCollectionEvidenceVersion;
    private static async Task ConcurrentEvidence(string root,bool corrupt)
    {
        var data=Path.Combine(root,"data");var rawPath=Path.Combine(root,"raw");var seedBytes="synthetic concurrent evidence seed"u8.ToArray();
        var seedHash=CRS.Common.Integrity.ContentFingerprint.Sha256(seedBytes);var incoming="synthetic concurrent market observation"u8.ToArray();
        var incomingHash=CRS.Common.Integrity.ContentFingerprint.Sha256(incoming);var enabled=false;var injected=0;var freshReads=0;var day=new DateOnly(2026,10,14);
        var raw=new RawEvidenceStore(rawPath,openRead:path=>
        {
            if(Volatile.Read(ref enabled)&&Path.GetFileName(path)==seedHash+".raw"&&Interlocked.Exchange(ref injected,1)==0)
            {
                using var other=TestMarketData.Open(data,new RawEvidenceStore(rawPath));
                other.SaveMarketActivity("SSE",day.AddDays(-1),new(new("https://www.sse.com.cn/synthetic-concurrent-activity"),incoming,"SSE",day.AddDays(-1),new("SSE",day.AddDays(-1),100,100)));
                if(corrupt)File.WriteAllBytes(Path.Combine(rawPath,incomingHash+".raw"),"damaged concurrent market observation"u8.ToArray());
            }
            if(Volatile.Read(ref enabled)&&Path.GetFileName(path)==incomingHash+".raw")Interlocked.Increment(ref freshReads);
            return File.OpenRead(path);
        });
        MarketRepository? repository=null;await using var app=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(data,raw),raw);
        await app.InitializeAsync(default);var key=new EtfKey("SSE","510300");
        foreach(var row in repository!.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"隔离验证");
        var fetch=repository.RecordFetch(raw.Save(seedBytes),"https://www.sse.com.cn/synthetic-concurrent-evidence-seed");
        repository.ImportCalendar(fetch,[(key.Market,day.AddDays(-1)),(key.Market,day)],default);repository.ImportShares(fetch,[new(key,day.AddDays(-1),100,"万份")],default);
        var source=new Source(key,day);app.Etf.SetOfficialSource(source);Volatile.Write(ref enabled,true);SyncReport? report=null;var failed=false;
        try{report=await app.Etf.SyncAsync(day,day,default);}catch(InvalidDataException){failed=true;}finally{Volatile.Write(ref enabled,false);}
        var attempt=(await app.Etf.CollectionAttemptsAsync(key,day,day,default)).First();
        if(injected!=1||freshReads==0||source.Calls!=1||failed!=corrupt)throw new Exception("提交未核验并行新增的原件，或错误放行了损坏。");
        if(!corrupt)
        {
            if(report?.ImportPreparationAttempts!=1||attempt.State!="Accepted")throw new Exception("完整并行成交原件导致了无谓的份额重算。");
            Console.WriteLine("PASS 份额提交：并行新增成交原件按当前库重新读验，不因全库版本变化退避");return;
        }
        if(attempt.State!="ParseFailed"||attempt.Reason!="ImportOrValidationFailed")throw new Exception("并行新增原件损坏仍返回成功。");
        Console.WriteLine("PASS 份额提交：并行新增且未登记质量问题的损坏原件仍阻止成功，不缩小完整性范围");
        File.WriteAllBytes(Path.Combine(rawPath,incomingHash+".raw"),incoming);await app.Etf.SyncAsync(day,day,default);
        if((await app.Etf.CollectionAttemptsAsync(key,day,day,default)).First().State!="Accepted"||(await app.Etf.PointsAsync(key,default)).Count!=2)throw new Exception("同哈希恢复后仍沿用旧损坏结论。");
        Console.WriteLine("PASS 份额提交：并行原件同哈希恢复后重新核验，原失败回执保留");
    }
    private static async Task PostFetchAdmission(string root)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=false;
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>Volatile.Read(ref block)?new Paused(File.OpenRead(path),entered,release):File.OpenRead(path));
        MarketRepository? repository=null;await using var app=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw),raw);
        await app.InitializeAsync(default);var key=new EtfKey("SSE","510300");var day=new DateOnly(2026,10,14);
        foreach(var row in repository!.Configuration().Where(x=>x.Market!=key.Market||x.Ticker!=key.Ticker))repository.SetMembership(new(row.Market,row.Ticker),row.Name,row.IndexGroup,false,"隔离验证");
        var fetch=repository.RecordFetch(raw.Save("synthetic post-fetch admission"u8.ToArray()),"https://www.sse.com.cn/synthetic-post-fetch-admission");
        repository.ImportCalendar(fetch,[(key.Market,day.AddDays(-1)),(key.Market,day)],default);repository.ImportShares(fetch,[new(key,day.AddDays(-1),100,"万份")],default);
        var source=new Source(key,day);using var readerStop=new CancellationTokenSource();Task? reading=null;var first=true;
        app.Etf.SetOfficialSource(new AwaitedSource(source,async()=>
        {if(first){first=false;Volatile.Write(ref block,true);reading=app.Etf.DashboardAsync(null,readerStop.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));}}));
        var deferred=false;
        try
        {
            try{await app.Etf.SyncAsync(day,day,default);}catch(CRS.Common.Concurrency.WorkAdmissionException error) when(error.Reason=="WaitBudget"){deferred=true;}
            if(!deferred||reading is null||reading.IsCompleted)throw new Exception("采集读准入未明确退避，或误取消了另一读操作。");
        }
        finally{Volatile.Write(ref block,false);readerStop.Cancel();release.TrySetResult();if(reading is not null)try{await reading;}catch(OperationCanceledException){}}
        var attempt=(await app.Etf.CollectionAttemptsAsync(key,day,day,default)).First();
        if(attempt.State!="Interrupted"||attempt.Reason!="AdmissionDeferred"||(await app.Etf.PointsAsync(key,default)).Count!=1)throw new Exception("下载后只读准入失败未保存中断状态，或伪造了新事实。");
        Console.WriteLine("PASS 份额提交：下载后读准入超预算保存中断，未记解析失败或取消其它读取");
        await app.Etf.SyncAsync(day,day,default);
        if((await app.Etf.PointsAsync(key,default)).Count!=2||source.Calls!=2)throw new Exception("准入恢复后未正常提交，或重复请求次数异常。");
        Console.WriteLine("PASS 份额提交：读名额释放后正常恢复并保留原中断回执");
    }
    private sealed class AwaitedSource(IOfficialShareSource inner,Func<Task> beforeFetch):IOfficialShareSource
    {public async Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token){await beforeFetch();return await inner.FetchAsync(day,token);}}
    private static void VersionCapabilities(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw);
        var port=(IEtfCollectionEvidenceRepository)repository;var version=((IMarketReadSessions)repository).Read(r=>((IEtfCollectionEvidenceRepository)r).CaptureCollectionEvidenceVersion(),default);var rejected=false;
        try{port.RequireCollectionEvidenceVersion(version);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("没有原件租约仍接受版本。");
        using(raw.HoldVerifiedEvidence())
        {
            rejected=false;try{port.RequireCollectionEvidenceVersion(new ForeignVersion());}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("接受了调用者填写的版本。");
            var otherRaw=new RawEvidenceStore(Path.Combine(root,"other-raw"));using var other=TestMarketData.Open(Path.Combine(root,"other-data"),otherRaw);
            using(otherRaw.HoldVerifiedEvidence())
            {rejected=false;try{((IEtfCollectionEvidenceRepository)other).RequireCollectionEvidenceVersion(version);}catch(InvalidDataException){rejected=true;}if(!rejected)throw new Exception("跨库接受了旧版本。");}
            port.RequireCollectionEvidenceVersion(version);rejected=false;try{port.RequireCollectionEvidenceVersion(version);}catch(InvalidOperationException){rejected=true;}
            if(!rejected)throw new Exception("同一份额版本被重复使用。");
        }
        rejected=false;try{port.ValidateCollectionEvidence();}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("预读被放在原写连接执行。");
        Console.WriteLine("PASS 份额提交：版本必须具备活动原件租约，拒绝伪造、跨库、重复使用及写连接预读");
    }
    private static async Task RawCaptureCancellation(string root)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pause=false;
        var raw=new RawEvidenceStore(root,openRead:path=>pause?new Paused(File.OpenRead(path),entered,release):File.OpenRead(path));var bytes="synthetic cancellable capture"u8.ToArray();var hash=raw.Save(bytes);
        pause=true;using var stopping=new CancellationTokenSource();var saving=Task.Run(()=>raw.Save(bytes,stopping.Token));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.Cancel();try{await saving.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("重复原件核验忽略取消。");}catch(OperationCanceledException){}finally{pause=false;release.TrySetResult();}
        var path=Path.Combine(root,hash+".raw");using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        if(!File.ReadAllBytes(path).SequenceEqual(bytes)||Directory.GetFiles(root,"*.tmp").Length!=0)throw new Exception("取消改变了已有原件或残留暂存文件。");
        Console.WriteLine("PASS 份额提交：重复原件保存可取消，保留原字节并释放文件及暂存");
    }
    private sealed class Source(EtfKey key,DateOnly day):IOfficialShareSource
    {
        public int Calls;
        public Task<SourceDay> FetchAsync(DateOnly date,CancellationToken token)
        {Calls++;return Task.FromResult(new SourceDay(day,[new(new("https://www.sse.com.cn/synthetic-collection-page"),Encoding.UTF8.GetBytes("synthetic collection incoming"),[new(key,day,101,"万份")])]));}
    }
    private sealed class Paused(Stream inner,TaskCompletionSource entered,TaskCompletionSource release):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>inner.CanSeek;public override bool CanWrite=>false;public override long Length=>inner.Length;
        public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken token=default){entered.TrySetResult();await release.Task.WaitAsync(token);return await inner.ReadAsync(bytes,token);}
        public override int Read(byte[] bytes,int offset,int count)=>inner.Read(bytes,offset,count);public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);
        public override void Flush()=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
