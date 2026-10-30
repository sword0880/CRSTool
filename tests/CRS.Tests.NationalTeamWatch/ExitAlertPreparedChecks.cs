using System.Reflection;
using System.Text.Json;
using CRS.Common.Concurrency;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>同一合成基线比较旧评价与准备/提交；真实文件锁、版本变更、取消及事务故障反例。</summary>
internal static class ExitAlertPreparedChecks
{
    private static int passed;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS 评价拆分："+message);}
    public static async Task RunAsync(string root)
    {
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("验收目录已存在。");Directory.CreateDirectory(root);passed=0;
        var seed=Path.Combine(root,"seed");var clock=new ExitAlertChecks.ManualClock();
        using(var repository=ExitAlertChecks.Setup(seed,clock,out _)){}
        var legacyRoot=Copy(seed,root,"legacy");var preparedRoot=Copy(seed,root,"prepared");
        using(var legacy=Open(legacyRoot,clock,out _))legacy.EvaluateExitAlerts(true);
        using(var repository=Open(preparedRoot,clock,out var raw))
        {
            var port=(IExitAlertEvaluationRepository)repository;
            using(raw.HoldVerifiedEvidence())
            using(var plan=Prepare(repository,true)??throw new Exception("没有准备结果。"))
            {
                Check(Count(preparedRoot,"nt_risk_event")==0&&Count(preparedRoot,"nt_alert_outbox")==0,"准备阶段不修改正式事件或Outbox");
                var path=Directory.GetFiles(Path.Combine(preparedRoot,"raw"),"*.raw").First();
                var denied=false;try{using var write=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);}catch(IOException){denied=true;}
                Check(denied,"已核原件在准备到提交期间由内核禁止写入");
                denied=false;try{File.Delete(path);}catch(IOException){denied=true;}Check(denied,"原件不能在提交前删除或替换");
                port.CommitExitAlertEvaluation(plan);
                var duplicate=false;try{port.CommitExitAlertEvaluation(plan);}catch(InvalidOperationException){duplicate=true;}Check(duplicate,"同一准备结果不能二次提交");
            }
            var tables=new[]{"nt_risk_event","nt_risk_event_change","nt_alert_input_snapshot","nt_alert_snapshot_revision","nt_alert_snapshot_source","nt_alert_notification_input","nt_alert_outbox","nt_alert_evaluation","nt_alert_pending"};
            foreach(var table in tables)Check(Rows(legacyRoot,table)==Rows(preparedRoot,table),"相同基线的旧/新评价持久内容一致："+table);
            using(raw.HoldVerifiedEvidence())using(var plan=Prepare(repository,true)!)port.CommitExitAlertEvaluation(plan);
            Check(Rows(legacyRoot,"nt_risk_event_change")==Rows(preparedRoot,"nt_risk_event_change")&&Rows(legacyRoot,"nt_alert_outbox")==Rows(preparedRoot,"nt_alert_outbox"),"重复评价无新增事件或通知");
        }
        foreach(var external in new[]{false,true})
        {
            var directory=Copy(seed,root,external?"external-change":"writer-change");using var repository=Open(directory,clock,out var raw);var port=(IExitAlertEvaluationRepository)repository;
            using(raw.HoldVerifiedEvidence())using(var plan=Prepare(repository,true)!)
            {
                if(external)Sql(directory,"UPDATE nt_alert_configuration SET revision=revision+1");
                else{var row=repository.Configuration().First();repository.SetMembership(new(row.Market,row.Ticker),row.Name+" changed",row.IndexGroup,row.Active,"synthetic");}
                var rejected=false;try{port.CommitExitAlertEvaluation(plan);}catch(WorkAdmissionException error) when(error.Reason=="InputsChanged"){rejected=true;}
                Check(rejected&&Count(directory,"nt_risk_event")==0&&Count(directory,"nt_alert_pending")==1,(external?"其它连接":"原写连接")+"变更使旧准备结果失效，保留待评价标记");
            }
        }
        var rollbackRoot=Copy(seed,root,"rollback");using(var repository=Open(rollbackRoot,clock,out var raw))
        using(raw.HoldVerifiedEvidence())using(var plan=Prepare(repository,true)!)
        {
            // 在最后追加合法表上的主键冲突，固定发生在前面事件/Outbox已执行之后。
            plan.GetType().GetMethod("Record",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plan,["INSERT INTO nt_alert_evaluation VALUES(1,'synthetic-conflict')",Array.Empty<(string,object?)>()]);
            var failed=false;try{((IExitAlertEvaluationRepository)repository).CommitExitAlertEvaluation(plan);}catch(SqliteException){failed=true;}
            Check(failed&&Count(rollbackRoot,"nt_risk_event")==0&&Count(rollbackRoot,"nt_alert_outbox")==0&&Count(rollbackRoot,"nt_risk_event_change")==0&&Count(rollbackRoot,"nt_alert_pending")==1,"提交尾部故障整体回滚事件、冻结输入、通知和待评价状态");
        }
        var foreignRoot=Copy(seed,root,"foreign");using(var repository=Open(foreignRoot,clock,out var raw))
        using(var other=Open(Copy(seed,root,"other"),clock,out _))
        using(raw.HoldVerifiedEvidence())using(var plan=Prepare(repository,true)!)
        {
            var rejected=false;try{((IExitAlertEvaluationRepository)other).CommitExitAlertEvaluation(plan);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"另一资料库不能提交该准备结果");
        }
        await ConcurrentPreparation(Copy(seed,root,"concurrent"),clock,false);
        await ConcurrentPreparation(Copy(seed,root,"cancel"),clock,true);
        await RepeatedChanges(Copy(seed,root,"continuous-change"),clock);
        await LeaseBoundaries(Path.Combine(root,"lease"));
        await LeaseScale(Path.Combine(root,"lease-scale"));
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticPreparedAlertEvaluation",FinancialAcceptance=false,WholeGoalComplete=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS 评价拆分合计{passed}；合成资料，不代表真实金融模型或正式部署。");
    }
    private static async Task ConcurrentPreparation(string root,ExitAlertChecks.ManualClock clock,bool cancel)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=false;
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>block?new Paused(File.OpenRead(path),entered,release):File.OpenRead(path));
        MarketRepository? repository=null;
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>repository=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock),raw);
        await workspace.InitializeAsync(default);var row=repository!.Configuration().First(x=>x.Active);var key=new CRS.Plugins.NationalTeamWatch.Domain.EtfKey(row.Market,row.Ticker);
        block=true;using var stop=new CancellationTokenSource();var evaluation=workspace.Alerts.EvaluateAlertsAsync(true,stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var configured=await workspace.Etf.ConfigurationAsync(default).WaitAsync(TimeSpan.FromSeconds(1));
            Check(configured.Any(x=>x.Market==key.Market&&x.Ticker==key.Ticker)&&!evaluation.IsCompleted,"评价长读暂停时采集配置读仍独立完成，不争用背景读名额");
            await workspace.Etf.SaveConfigurationAsync(key,"并发更名",row.IndexGroup,true,"synthetic",row.Revision,default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(!evaluation.IsCompleted,"原件读取仍暂停时配置写入已独立提交");
            if(cancel)
            {
                stop.Cancel();try{await evaluation.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("取消被报告成功。");}catch(OperationCanceledException){}
                Check(Count(root,"nt_risk_event")==0&&Count(root,"nt_alert_outbox")==0,"准备期间取消不留下事件或通知");
            }
            else
            {
                block=false;release.TrySetResult();var metrics=await evaluation.WaitAsync(TimeSpan.FromSeconds(10));
                Check(repository.AlertEvents(new("EtfOutflow")).Single().Headline.Contains("并发更名"),"提交前版本变更自动重新准备，未写入旧名称结论");
                Check(metrics.Attempts>=2&&!metrics.Skipped&&metrics.CommitHeldMilliseconds>=metrics.CommitBodyMilliseconds,"回执区分准备、提交体和实际持有写入名额的时间");
            }
        }
        finally{block=false;release.TrySetResult();stop.Cancel();try{await evaluation;}catch(OperationCanceledException){}}
        foreach(var path in Directory.GetFiles(Path.Combine(root,"raw"),"*.raw"))using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
        Check(repository.Configuration().Single(x=>x.Market==key.Market&&x.Ticker==key.Ticker).Name=="并发更名","取消/完成后原件锁释放，原写入保留");
    }
    private static async Task LeaseBoundaries(string root)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,"one");var content="stable synthetic"u8.ToArray();File.WriteAllBytes(path,content);var hash=ContentFingerprint.Sha256(content);var reads=0;
        await using(var lease=new VerifiedContentLease(1))
        {
            Check(await lease.VerifyAsync(path,hash,1024,default,_=>{reads++;return Task.CompletedTask;})&&await lease.VerifyAsync(path,hash,1024,default)&&reads==1&&lease.RetainedFiles==1,"同租约仅完整读验一次且真实保留句柄");
            Check(!await lease.VerifyAsync(path,hash,1,default),"更小的字节上限不能借已核结果放宽");
            var bounded=false;try{await lease.VerifyAsync(Path.Combine(root,"two"),hash,1024,default);}catch(InvalidDataException){bounded=true;}Check(bounded,"稳定租约文件数量有明确上限");
        }
        File.WriteAllText(path,"damaged");await using(var next=new VerifiedContentLease())Check(!await next.VerifyAsync(path,hash,1024,default),"下一租约重新读字节，拒绝跨请求复用旧核验");
        File.WriteAllBytes(path,content);var pinned=new VerifiedContentLease();await pinned.VerifyAsync(path,hash,1024,default);
        var commit=pinned.PinForCommit();var disposing=pinned.DisposeAsync().AsTask();var second=pinned.DisposeAsync().AsTask();
        var denied=false;try{File.WriteAllText(path,"cannot replace during commit");}catch(IOException){denied=true;}
        Check(denied&&!disposing.IsCompleted&&!second.IsCompleted,"所有者取消/重复释放也等待正在提交的原件句柄");
        commit.Dispose();await Task.WhenAll(disposing,second).WaitAsync(TimeSpan.FromSeconds(2));File.WriteAllBytes(path,content);
        Check(pinned.RetainedFiles==0,"短提交结束后释放全部文件句柄");
    }
    private static async Task RepeatedChanges(string root,ExitAlertChecks.ManualClock clock)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));var change=false;var attempts=0;
        await using var workspace=NationalTeamApplicationComposition.Create(new QueryOnlyContext(),()=>TestMarketData.Open(Path.Combine(root,"data"),raw,()=>
        {if(change){attempts++;Sql(root,"UPDATE nt_alert_configuration SET revision=revision+1");}},clock),raw);
        await workspace.InitializeAsync(default);change=true;var deferred=false;
        try{await workspace.Alerts.EvaluateAlertsAsync(true,default);}catch(WorkAdmissionException error) when(error.Reason=="InputsChanged"){deferred=true;}
        Check(deferred&&attempts==3&&Count(root,"nt_risk_event")==0&&Count(root,"nt_alert_pending")==1,"持续变更只重算三次后明确退避，不无限循环或提交旧结果");
    }
    private static IPreparedExitAlertEvaluation? Prepare(MarketRepository repository,bool force)
    {var version=((IExitAlertEvaluationRepository)repository).CaptureExitAlertEvaluationVersion();return ((IMarketReadSessions)repository).Read(reader=>((IExitAlertEvaluationRepository)reader).PrepareExitAlertEvaluation(version,force),default);}
    private static async Task LeaseScale(string root)
    {
        Directory.CreateDirectory(root);var files=Enumerable.Range(0,6000).Select(i=>
        {var content=System.Text.Encoding.UTF8.GetBytes("synthetic leased evidence "+i);var path=Path.Combine(root,i+".raw");File.WriteAllBytes(path,content);return(Path:path,Hash:ContentFingerprint.Sha256(content));}).ToArray();
        var lease=new VerifiedContentLease();
        try
        {
            await Parallel.ForEachAsync(files,new ParallelOptions{MaxDegreeOfParallelism=8},async(item,token)=>
            {if(!await lease.VerifyAsync(item.Path,item.Hash,1024,token))throw new Exception("规模原件核验失败。");});
            Check(lease.RetainedFiles==6000,"六千份不同原件在有界并行读验后保持稳定句柄");
        }
        finally{await lease.DisposeAsync();}
        Check(lease.RetainedFiles==0,"规模租约结束不保留文件句柄");
    }
    private static MarketRepository Open(string root,ExitAlertChecks.ManualClock clock,out RawEvidenceStore raw)
    {raw=new(Path.Combine(root,"raw"));return TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);}
    private static string Copy(string source,string root,string name)
    {var target=Path.Combine(root,name);foreach(var path in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){var copy=Path.Combine(target,Path.GetRelativePath(source,path));Directory.CreateDirectory(Path.GetDirectoryName(copy)!);File.Copy(path,copy);}return target;}
    private static SqliteConnection Database(string root){var db=new SqliteConnection($"Data Source={Path.Combine(root,"data","market.db")};Pooling=False");db.Open();return db;}
    private static long Count(string root,string table){using var db=Database(root);using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM "+table;return Convert.ToInt64(command.ExecuteScalar());}
    private static void Sql(string root,string sql){using var db=Database(root);using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static string Rows(string root,string table)
    {using var db=Database(root);using var command=db.CreateCommand();command.CommandText="SELECT * FROM "+table;using var reader=command.ExecuteReader();var rows=new List<string>();while(reader.Read()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(JsonSerializer.Serialize(values.Select(x=>x is DBNull?null:x)));}return JsonSerializer.Serialize(rows.Order(StringComparer.Ordinal));}
    private sealed class Paused(Stream stream,TaskCompletionSource entered,TaskCompletionSource release):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>stream.CanSeek;public override bool CanWrite=>false;public override long Length=>stream.Length;public override long Position{get=>stream.Position;set=>stream.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){entered.TrySetResult();await release.Task.WaitAsync(token);return await stream.ReadAsync(buffer,token);}
        public override int Read(byte[] buffer,int offset,int count)=>stream.Read(buffer,offset,count);public override long Seek(long offset,SeekOrigin origin)=>stream.Seek(offset,origin);public override void Flush()=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();protected override void Dispose(bool disposing){if(disposing)stream.Dispose();base.Dispose(disposing);}
    }
}
