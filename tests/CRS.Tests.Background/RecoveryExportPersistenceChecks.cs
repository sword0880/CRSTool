using System.Diagnostics;
using System.Security.Cryptography;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

/// <summary>真实加密文件与子进程故障验证；不访问正式数据，不代表跨机恢复验收。</summary>
internal static class RecoveryExportPersistenceChecks
{
    private static readonly BackgroundCaller Admin=new("S-1-5-21-1-2-3-1000",true);
    private static int passed;
    internal static async Task RunAsync(string root)
    {
        passed=0;StorageSecurity.Prepare(root,true);
        var key=RandomNumberGenerator.GetBytes(32);var secret=RandomNumberGenerator.GetBytes(32);
        try
        {
            await RoundTripAndReplay(Path.Combine(root,"roundtrip"),key,secret);
            QuotasAndExpiry(Path.Combine(root,"quotas"),key);
            StorageFailure(Path.Combine(root,"fault"),key,secret);
            RejectedAttempts(Path.Combine(root,"rejected"),key,secret);
            foreach(var phase in new[]{"Prepared","Authorized","Sealed"})
                await RestartAfterKill(Path.Combine(root,"restart-"+phase),key,secret,phase);
            MissingAndCorruptIndex(Path.Combine(root,"damaged"),key);
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(secret);}
        Console.WriteLine($"恢复授权持久化验证通过：{passed} 项；加密往返、跨重启重放、容量、故障关闭与真实强杀重启。");
    }

    private static async Task RoundTripAndReplay(string directory,byte[] key,byte[] secret)
    {
        Directory.CreateDirectory(directory);var prepare=Guid.NewGuid();var bundle=Guid.NewGuid();var confirm=Guid.NewGuid();
        var digest=SHA256.HashData("synthetic frozen manifest"u8);var reads=0;
        using(var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>throw new Exception("意外存储故障。")))
        {
            var challenge=coordinator.Prepare(Admin,prepare,bundle,digest,()=>{Interlocked.Increment(ref reads);return key.ToArray();});
            var replies=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>
            {
                try{return coordinator.Confirm(Admin,prepare,confirm,bundle,digest,challenge,secret,true);}
                catch(RecoveryExportRefusedException error) when(error.Code==RecoveryExportFailure.Consumed){return null;}
            })));
            var envelope=replies.Single(x=>x is not null)!;
            using var recovered=ServiceKeyRecoveryEnvelope.Open(envelope,secret,bundle,digest);var copy=recovered.Copy();
            try{Check(reads==1&&copy.SequenceEqual(key),"并发确认只有一次读取原钥，返回封装可认证解密");}
            finally{CryptographicOperations.ZeroMemory(copy);CryptographicOperations.ZeroMemory(challenge);}
            Refuses(()=>new DurableRecoveryExportCoordinator(directory,key,()=>{}).Dispose(),"同目录拒绝第二个授权协调器",typeof(IOException));
        }
        using(var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>{}))
        {
            Refuses(()=>coordinator.Prepare(Admin,prepare,Guid.NewGuid(),digest,()=>key.ToArray()),"准备请求跨重启不能复用");
            Refuses(()=>coordinator.Prepare(Admin,Guid.NewGuid(),bundle,digest,()=>key.ToArray()),"包编号跨重启不能复用");
            var next=Guid.NewGuid();var nextBundle=Guid.NewGuid();var challenge=coordinator.Prepare(Admin,next,nextBundle,digest,()=>key.ToArray());
            Refuses(()=>coordinator.Confirm(Admin,next,confirm,nextBundle,digest,challenge,secret,true),"确认编号不能跨操作重放");
        }
        // 审计库采用独立派生域；普通 SQLite 和错误密钥都不能读出授权记录。
        using(var plain=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"recovery-authorizations.db"),Pooling=false}.ToString()))
            Refuses(()=>{plain.Open();using var command=plain.CreateCommand();command.CommandText="SELECT count(*) FROM recovery_attempt";command.ExecuteScalar();},"授权记录没有明文落盘",typeof(SqliteException));
        var wrong=RandomNumberGenerator.GetBytes(32);
        try{Refuses(()=>new DurableRecoveryExportCoordinator(directory,wrong,()=>{}).Dispose(),"错误审计密钥失败且不重建索引",typeof(SqliteException));}
        finally{CryptographicOperations.ZeroMemory(wrong);}
    }

    private static void QuotasAndExpiry(string directory,byte[] key)
    {
        Directory.CreateDirectory(directory);var clock=new MonotonicClock();
        using(var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>{},clock))
        {
            var digest=new byte[32];coordinator.Prepare(Admin,Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray());
            Refuses(()=>coordinator.Prepare(Admin,Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray()),"同一身份只保留一个待确认会话");
            for(var i=1;i<4;i++)coordinator.Prepare(Admin with{Sid=Admin.Sid+i},Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray());
            Refuses(()=>coordinator.Prepare(Admin with{Sid=Admin.Sid+4},Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray()),"全局待确认会话上限为四个");
            clock.Timestamp+=RecoveryExportSession.Lifetime.Ticks;
            coordinator.Prepare(Admin,Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray());
            Check(true,"过期会话持久拒绝后释放活动容量");
            Refuses(()=>coordinator.Prepare(Admin with{CanManage=false},Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray()),"非管理身份不能创建持久会话");
        }
        // 使用真实加密库预置已占用索引，验证总容量达到上限时拒绝，历史编号不被清理。
        using(var db=OpenAudit(directory,key))
        {
            using var command=db.CreateCommand();command.CommandText="""
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count)
                INSERT INTO recovery_attempt SELECT 'fixture-'||x,'bundle-'||x,'fixture',zeroblob(32),'Rejected' FROM n;
                """;command.Parameters.AddWithValue("$count",RecoveryExportJournal.MaximumAttempts);command.ExecuteNonQuery();
        }
        using var full=new DurableRecoveryExportCoordinator(directory,key,()=>{});
        Refuses(()=>full.Prepare(Admin,Guid.NewGuid(),Guid.NewGuid(),new byte[32],()=>key.ToArray()),"总索引有界且不删除旧编号换取容量");
    }

    private static void StorageFailure(string directory,byte[] key,byte[] secret)
    {
        Directory.CreateDirectory(directory);var faults=0;var reads=0;var prepare=Guid.NewGuid();var bundle=Guid.NewGuid();var digest=new byte[32];
        using var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>faults++);
        var challenge=coordinator.Prepare(Admin,prepare,bundle,digest,()=>{reads++;return key.ToArray();});
        using(var blocker=OpenAudit(directory,key))
        using(var transaction=blocker.BeginTransaction())
            Refuses(()=>coordinator.Confirm(Admin,prepare,Guid.NewGuid(),bundle,digest,challenge,secret,true),"审计写入锁超时不返回封装",typeof(SqliteException));
        Check(faults==1&&reads==0,"审计故障触发宿主故障回调，原钥尚未读取");
        Refuses(()=>coordinator.Prepare(Admin,Guid.NewGuid(),Guid.NewGuid(),digest,()=>key.ToArray()),"故障后拒绝新授权",typeof(InvalidDataException));
    }

    private static void RejectedAttempts(string directory,byte[] key,byte[] secret)
    {
        Directory.CreateDirectory(directory);
        foreach(var failure in new[]{"Identity","Confirmation","Cancellation"})
        {
            var prepare=Guid.NewGuid();var bundle=Guid.NewGuid();var reads=0;var digest=new byte[32];
            using(var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>{}))
            {
                var challenge=coordinator.Prepare(Admin,prepare,bundle,digest,()=>{reads++;return key.ToArray();});
                Refuses(()=>coordinator.Confirm(failure=="Identity"?Admin with{Sid=Admin.Sid+1}:Admin,
                    prepare,Guid.NewGuid(),bundle,digest,challenge,secret,failure!="Confirmation",new CancellationToken(failure=="Cancellation")),
                    failure+" 拒绝确认");
            }
            using var journal=new RecoveryExportJournal(directory,key);
            Check(reads==0&&journal.State(prepare)=="Rejected",failure+" 拒绝持久落盘且未读取原钥");
        }
    }

    private static async Task RestartAfterKill(string directory,byte[] key,byte[] secret,string phase)
    {
        Directory.CreateDirectory(directory);var prepare=Guid.NewGuid();var bundle=Guid.NewGuid();
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var value in new[]{"--recovery-journal-child",directory,prepare.ToString("N"),bundle.ToString("N"),phase})start.ArgumentList.Add(value);
        using var child=Process.Start(start)??throw new Exception("隔离进程未启动。");
        try
        {
            // 测试密钥通过私有标准输入传递；argv、环境、临时文件及输出都不携带秘密。
            await child.StandardInput.BaseStream.WriteAsync(key);await child.StandardInput.BaseStream.FlushAsync();child.StandardInput.Close();
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))==phase,"子进程已持久记录 "+phase+" 阶段");
            child.Kill(entireProcessTree:true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}}
        using(var journal=new RecoveryExportJournal(directory,key))Check(journal.State(prepare)==(phase=="Sealed"?"Sealed":"Abandoned"),phase+" 强杀后保留真实阶段，不自动重做封装");
        using var restarted=new DurableRecoveryExportCoordinator(directory,key,()=>{});
        Refuses(()=>restarted.Prepare(Admin,prepare,bundle,new byte[32],()=>key.ToArray()),"强杀重启后仍拒绝旧准备请求");
        Refuses(()=>restarted.Confirm(Admin,prepare,Guid.NewGuid(),bundle,new byte[32],new byte[32],secret,true),"强杀重启后不能恢复内存确认挑战");
    }

    internal static async Task ChildAsync(string directory,Guid prepare,Guid bundle,string phase)
    {
        if(phase is not ("Prepared" or "Authorized" or "Sealed"))throw new ArgumentException("未知测试阶段。");
        var key=new byte[32];await Console.OpenStandardInput().ReadExactlyAsync(key);
        var secret=RandomNumberGenerator.GetBytes(32);
        try
        {
            using var coordinator=new DurableRecoveryExportCoordinator(directory,key,()=>{});
            var challenge=coordinator.Prepare(Admin,prepare,bundle,new byte[32],()=>
            {
                // 在原钥回调阻塞，可精确强杀于 Authorized 持久化之后、封装之前。
                if(phase=="Authorized"){Console.WriteLine(phase);Thread.Sleep(Timeout.Infinite);}
                return key.ToArray();
            });
            if(phase!="Prepared")
                _=coordinator.Confirm(Admin,prepare,Guid.NewGuid(),bundle,new byte[32],challenge,secret,true);
            Console.WriteLine(phase);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(secret);}
    }

    private static void MissingAndCorruptIndex(string directory,byte[] key)
    {
        Directory.CreateDirectory(directory);
        using(var journal=new RecoveryExportJournal(directory,key)){}
        var path=Path.Combine(directory,"recovery-authorizations.db");File.Delete(path);
        Refuses(()=>new RecoveryExportJournal(directory,key).Dispose(),"已有初始化标记但索引丢失不能重建",typeof(InvalidDataException));
        Check(!File.Exists(path),"缺失索引拒绝后没有创建空库");
        var corrupt=Path.Combine(directory,"corrupt");Directory.CreateDirectory(corrupt);
        using(var journal=new RecoveryExportJournal(corrupt,key)){}
        using(var db=OpenAudit(corrupt,key)){using var command=db.CreateCommand();command.CommandText="PRAGMA user_version=99";command.ExecuteNonQuery();}
        Refuses(()=>new RecoveryExportJournal(corrupt,key).Dispose(),"未知审计 Schema 失败关闭",typeof(InvalidDataException));
    }

    private static SqliteConnection OpenAudit(string directory,byte[] master)
    {
        var key=HKDF.DeriveKey(HashAlgorithmName.SHA256,master,32,info:"CRS.Worker.RecoveryAudit/v1"u8.ToArray());
        try{var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"recovery-authorizations.db"),Password=Convert.ToHexString(key),Pooling=false}.ToString());db.Open();return db;}
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS 恢复持久化："+message);}
    private static void Refuses(Action work,string message,Type? type=null)
    {
        try{work();}catch(Exception error) when(type is null?error is RecoveryExportRefusedException:type.IsInstanceOfType(error)){Check(true,message);return;}
        throw new Exception(message+"：意外接受。");
    }
    private sealed class MonotonicClock:TimeProvider
    {public long Timestamp;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>Timestamp;}
}
