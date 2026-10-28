using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.IO;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

internal static class NationalTeamMigrationChecks
{
    private sealed record Fixture(string Live,string Snapshot,IReadOnlyDictionary<string,string> Original);
    private sealed record TestRecord(string Name,int Value);
    private sealed class InjectedFailure:Exception{}
    private static int passed;
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);passed=0;Common(Path.Combine(root,"common"));
        var first=Create(Path.Combine(root,"complete"));var id=Guid.NewGuid();var result=new NationalTeamPlaintextMigration().Run(first.Live,first.Snapshot,id,TestMarketData.Key);
        Check(result.State=="Completed"&&result.ConvertedDatabases==2&&result.Files.All(x=>x.Logical is not null),"双库都准备、验证并发布，返回各自逻辑摘要");
        var market=Path.Combine(first.Live,"data","market.db");using(var db=Open(market))
        {
            Check(Convert.ToInt64(Scalar(db,"SELECT seq FROM sqlite_sequence WHERE name='facts'"))==100,"自增高水位保留，删除最高ID后也不能重用旧序号");
            Execute(db,"INSERT INTO facts(value) VALUES('new-after-migration')");Check(Convert.ToInt64(Scalar(db,"SELECT max(id) FROM facts"))==101,"迁移后新增记录从原高水位继续");
        }
        var changed=Hash(market);var replay=new NationalTeamPlaintextMigration().Run(first.Live,first.Snapshot,id,TestMarketData.Key);
        Check(replay.State=="AlreadyCompleted"&&Hash(market)==changed,"完成后重放不拿旧摘要或旧库覆盖合法新增事实");Preserved(first,id);
        foreach(var point in new[]{"JournalCreated","FilePrepared:data/market.db","Prepared","OriginalMoved:data/market.db","PlainPublished:data/market.db","OriginalMoved:data/source-policy.db","PlainPublished:data/source-policy.db","Completed"})
        {
            var fixture=Create(Path.Combine(root,"interrupt-"+passed));var operation=Guid.NewGuid();var hit=false;
            try{new NationalTeamPlaintextMigration(stage=>{if(stage==point){hit=true;throw new InjectedFailure();}}).Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key);throw new Exception("没有到达中断点");}catch(InjectedFailure){}
            Check(hit,"到达独立中断点："+point);
            Check(new NationalTeamPlaintextMigration().Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key).State is "Completed" or "AlreadyCompleted","同操作身份能续做："+point);Preserved(fixture,operation);
        }
        var wrong=Create(Path.Combine(root,"wrong-key"));Reject(()=>new NationalTeamPlaintextMigration().Run(wrong.Live,wrong.Snapshot,Guid.NewGuid(),new byte[32]),"错误密钥不切换原库");Unchanged(wrong);
        var second=Create(Path.Combine(root,"second-wrong-key"),policyKey:Enumerable.Repeat((byte)19,32).ToArray());Reject(()=>new NationalTeamPlaintextMigration().Run(second.Live,second.Snapshot,Guid.NewGuid(),TestMarketData.Key),"第二库无法解密时，第一库准备成功也不发布");Unchanged(second);
        var mixed=Create(Path.Combine(root,"mixed"),plainPolicy:true);var policyHash=Hash(Path.Combine(mixed.Live,"data","source-policy.db"));
        Check(new NationalTeamPlaintextMigration().Run(mixed.Live,mixed.Snapshot,Guid.NewGuid(),TestMarketData.Key).ConvertedDatabases==1&&Hash(Path.Combine(mixed.Live,"data","source-policy.db"))==policyHash,"混合格式只转旧加密库，已有普通库保持原字节");
        var plain=Create(Path.Combine(root,"plain"),plainMarket:true,plainPolicy:true);Check(new NationalTeamPlaintextMigration().Run(plain.Live,plain.Snapshot,Guid.NewGuid(),TestMarketData.Key).ConvertedDatabases==0,"全部为普通库时不进行无意义转换");Unchanged(plain);
        var busy=Create(Path.Combine(root,"busy"));using(var lease=new FileStream(Path.Combine(busy.Live,"runner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))Reject(()=>new NationalTeamPlaintextMigration().Run(busy.Live,busy.Snapshot,Guid.NewGuid(),TestMarketData.Key),"正常Runner占用目录时拒绝迁移");Unchanged(busy);
        var canceled=Create(Path.Combine(root,"cancel"));using(var cancellation=new CancellationTokenSource())
        {
            var operation=Guid.NewGuid();try{new NationalTeamPlaintextMigration(stage=>{if(stage=="Prepared")cancellation.Cancel();}).Run(canceled.Live,canceled.Snapshot,operation,TestMarketData.Key,cancellation.Token);throw new Exception("取消未生效");}catch(OperationCanceledException){Check(true,"准备完成后取消也不会进入替换");}
            Unchanged(canceled);Check(new NationalTeamPlaintextMigration().Run(canceled.Live,canceled.Snapshot,operation,TestMarketData.Key).State=="Completed","取消后的同操作可恢复");
        }
        Tampering(Path.Combine(root,"tampering"));
        var rollback=Create(Path.Combine(root,"rollback"));File.WriteAllText(Path.Combine(rollback.Live,"data","market.db-journal"),"unsupported-synthetic-rollback");File.WriteAllText(Path.Combine(rollback.Snapshot,"data","market.db-journal"),"unsupported-synthetic-rollback");
        Reject(()=>new NationalTeamPlaintextMigration().Run(rollback.Live,rollback.Snapshot,Guid.NewGuid(),TestMarketData.Key),"不忽略未支持的回滚日志");Unchanged(rollback);
        Wal();
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticTwoDatabaseMigrationAndAuthenticatedFiles",FormalServiceMigrationIntegrated=false},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void Common(string root)
    {
        Directory.CreateDirectory(root);var file=PhysicalPathGuard.Child(root,"missing/record.json");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AuthenticatedJsonFile.Write(file,TestMarketData.Key,"synthetic-purpose",new TestRecord("one",1));
        Check(AuthenticatedJsonFile.Read<TestRecord>(file,TestMarketData.Key,"synthetic-purpose")==new TestRecord("one",1),"共用认证JSON原子写入并严格回读");
        Reject(()=>AuthenticatedJsonFile.Read<TestRecord>(file,TestMarketData.Key,"other-purpose"),"认证文件不能跨用途使用");Reject(()=>AuthenticatedJsonFile.Read<TestRecord>(file,new byte[32],"synthetic-purpose"),"认证文件错误密钥拒绝");
        var envelope=JsonNode.Parse(File.ReadAllText(file))!;var body=Convert.FromBase64String(envelope["Body"]!.GetValue<string>());body[0]^=1;envelope["Body"]=Convert.ToBase64String(body);File.WriteAllText(file,envelope.ToJsonString());
        Reject(()=>AuthenticatedJsonFile.Read<TestRecord>(file,TestMarketData.Key,"synthetic-purpose"),"认证文件正文篡改拒绝");
        Reject(()=>PhysicalPathGuard.Child(root,"../outside"),"共用路径防护拒绝越界");
    }
    private static void Wal()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.NationalTeamMigration",Guid.NewGuid().ToString("N"));var fixture=Create(root);
        var info=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(NationalTeamMigrationChecks).Assembly.Location);
        info.ArgumentList.Add("--national-team-migration-wal-child");info.ArgumentList.Add(fixture.Live);
        using(var child=System.Diagnostics.Process.Start(info)??throw new Exception("无法启动隔离WAL夹具"))
        {
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(20);while(!File.Exists(Path.Combine(fixture.Live,"ready"))){if(child.HasExited)throw new Exception("WAL夹具提前退出："+child.StandardError.ReadToEnd());if(DateTime.UtcNow>deadline)throw new TimeoutException();Thread.Sleep(20);}
                child.Kill(entireProcessTree:true);if(!child.WaitForExit(10000))throw new TimeoutException();
            }
            finally{if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit(10000);}}
        }
        var originals=new Dictionary<string,string>();foreach(var file in Directory.GetFiles(Path.Combine(fixture.Live,"data"))){File.Copy(file,Path.Combine(fixture.Snapshot,"data",Path.GetFileName(file)),true);originals["data/"+Path.GetFileName(file)]=Hash(file);}fixture=fixture with{Original=originals};
        Check(originals.ContainsKey("data/source-policy.db-wal"),"真实强杀后留有已提交WAL");
        var operation=Guid.NewGuid();var stopped=false;
        try{new NationalTeamPlaintextMigration(s=>{if(s=="OriginalMoved:data/source-policy.db-wal"){stopped=true;throw new InjectedFailure();}}).Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key);}catch(InjectedFailure){}
        Check(stopped,"在旧WAL归档与主库替换之间实际中断");
        Check(new NationalTeamPlaintextMigration().Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key).State=="Completed","WAL已归档而主库未切换时仍可按原记录续做");
        using(var db=Open(Path.Combine(fixture.Live,"data","source-policy.db")))Check(Convert.ToInt32(Scalar(db,"SELECT failures FROM source_policy WHERE source='SSE'"))==13,"已提交但未检查点的来源冷却状态保留");Preserved(fixture,operation);
    }
    public static void WalChild(string directory)
    {
        directory=Path.GetFullPath(directory);var boundary=Path.Combine(Path.GetTempPath(),"CRS.NationalTeamMigration")+Path.DirectorySeparatorChar;
        if(!directory.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(directory)!="crs.national-team")throw new InvalidDataException("WAL夹具只接受独立临时根。");
        using var db=Open(Path.Combine(directory,"data","source-policy.db"),TestMarketData.Key);Execute(db,"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE source_policy SET failures=13;");File.WriteAllText(Path.Combine(directory,"ready"),"SyntheticCommittedWal");Thread.Sleep(Timeout.Infinite);
    }
    private static void Tampering(string root)
    {
        var fixture=Create(Path.Combine(root,"journal"));var operation=Guid.NewGuid();StopPrepared(fixture,operation);
        var journal=Path.Combine(fixture.Live,"storage-migrations",operation.ToString("N"),"migration.json");var envelope=JsonNode.Parse(File.ReadAllText(journal))!;envelope["Tag"]=Convert.ToBase64String(new byte[32]);File.WriteAllText(journal,envelope.ToJsonString());
        Reject(()=>new NationalTeamPlaintextMigration().Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key),"迁移日志认证失败不能继续切换");Unchanged(fixture);
        var changed=Create(Path.Combine(root,"changed-source"));var changedId=Guid.NewGuid();StopPrepared(changed,changedId);
        using(var db=Open(Path.Combine(changed.Live,"data","source-policy.db"),TestMarketData.Key))Execute(db,"UPDATE source_policy SET failures=99");var firstHash=Hash(Path.Combine(changed.Live,"data","market.db"));
        Reject(()=>new NationalTeamPlaintextMigration().Run(changed.Live,changed.Snapshot,changedId,TestMarketData.Key),"准备后第二库发生新变化时拒绝整组发布");
        Check(Hash(Path.Combine(changed.Live,"data","market.db"))==firstHash,"先验证全组，未提前移动第一库");
        var snapshot=Create(Path.Combine(root,"snapshot"));File.AppendAllText(Path.Combine(snapshot.Snapshot,"data","market.db"),"tampered");
        Reject(()=>new NationalTeamPlaintextMigration().Run(snapshot.Live,snapshot.Snapshot,Guid.NewGuid(),TestMarketData.Key),"快照与活动库不一致时拒绝初始化迁移");
        var target=Create(Path.Combine(root,"prepared-target"));var targetId=Guid.NewGuid();StopPrepared(target,targetId);var prepared=Directory.GetFiles(Path.Combine(target.Live,"storage-migrations",targetId.ToString("N"),"prepared"),"market.db",SearchOption.AllDirectories).Single();File.AppendAllText(prepared,"tampered");
        Reject(()=>new NationalTeamPlaintextMigration().Run(target.Live,target.Snapshot,targetId,TestMarketData.Key),"准备目标被篡改时拒绝发布");Unchanged(target);
    }
    private static void StopPrepared(Fixture fixture,Guid operation)
    {try{new NationalTeamPlaintextMigration(s=>{if(s=="Prepared")throw new InjectedFailure();}).Run(fixture.Live,fixture.Snapshot,operation,TestMarketData.Key);}catch(InjectedFailure){return;}throw new Exception("未中断准备阶段");}
    private static Fixture Create(string root,bool plainMarket=false,bool plainPolicy=false,byte[]? policyKey=null)
    {
        var live=Path.Combine(root,"live","crs.national-team");var snapshot=Path.Combine(root,"snapshot","crs.national-team");Directory.CreateDirectory(Path.Combine(live,"data"));Directory.CreateDirectory(Path.Combine(snapshot,"data"));
        using(var db=Open(Path.Combine(live,"data","market.db"),plainMarket?null:TestMarketData.Key))
            Execute(db,"PRAGMA foreign_keys=ON; PRAGMA user_version=2; CREATE TABLE plugin_schema_migration(version INTEGER PRIMARY KEY); INSERT INTO plugin_schema_migration VALUES(2); CREATE TABLE facts(id INTEGER PRIMARY KEY AUTOINCREMENT,value TEXT NOT NULL); INSERT INTO facts(value) VALUES('preserve'); INSERT INTO facts(id,value) VALUES(100,'deleted-high-water'); DELETE FROM facts WHERE id=100; CREATE TABLE child(id INTEGER PRIMARY KEY,parent INTEGER REFERENCES facts(id)); INSERT INTO child VALUES(1,1);");
        using(var db=Open(Path.Combine(live,"data","source-policy.db"),plainPolicy?null:policyKey??TestMarketData.Key))
            Execute(db,"PRAGMA user_version=1; CREATE TABLE source_policy(source TEXT PRIMARY KEY,next_utc TEXT NOT NULL,blocked_utc TEXT NOT NULL,failures INTEGER NOT NULL); INSERT INTO source_policy VALUES('SSE','2030-01-01T00:00:00.0000000+00:00','2030-01-02T00:00:00.0000000+00:00',7);");
        foreach(var file in Directory.GetFiles(Path.Combine(live,"data")))File.Copy(file,Path.Combine(snapshot,"data",Path.GetFileName(file)));
        return new(live,snapshot,Directory.GetFiles(Path.Combine(live,"data")).ToDictionary(x=>"data/"+Path.GetFileName(x),Hash));
    }
    private static void Preserved(Fixture fixture,Guid id)
    {
        Check(fixture.Original.All(x=>Hash(Path.Combine(fixture.Snapshot,x.Key))==x.Value&&Hash(Path.Combine(fixture.Live,"storage-migrations",id.ToString("N"),"original",x.Key))==x.Value),"原加密库及停机快照逐文件保留");
    }
    private static void Unchanged(Fixture fixture)=>Check(fixture.Original.All(x=>Hash(Path.Combine(fixture.Live,x.Key))==x.Value),"失败或无转换时原数据库字节未改变");
    private static SqliteConnection Open(string path,byte[]? key=null)
    {SQLitePCL.Batteries_V2.Init();var builder=new SqliteConnectionStringBuilder{DataSource=path,Pooling=false};if(key is not null)builder.Password=Convert.ToHexString(key);var connection=new SqliteConnection(builder.ToString());connection.Open();return connection;}
    private static object? Scalar(SqliteConnection db,string sql){using var command=db.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static void Execute(SqliteConnection db,string sql){using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static string Hash(string file){using var input=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(input));}
    private static void Check(bool condition,string text){if(!condition)throw new Exception(text);passed++;Console.WriteLine("PASS 国家队迁移："+text);}
    private static void Reject(Action action,string text){try{action();}catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or SqliteException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
