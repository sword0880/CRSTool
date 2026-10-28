using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

/// <summary>未经修改的完整候选执行真实国家队迁移入口，再经生产插件及客户端回读合成资料。</summary>
internal static class StorageMigrationPipeChecks
{
    public static async Task ProbeAsync(string pipe,string metadataPath,string output)
    {
        using var metadata=JsonDocument.Parse(File.ReadAllBytes(metadataPath));var seed=metadata.RootElement;
        var client=new BackgroundPipeClient(pipe);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var workspace=new WorkerEtfWorkspace(client);
        var configuration=await workspace.ConfigurationAsync(deadline.Token);var points=await workspace.PointsAsync(new(seed.GetProperty("ObservedMarket").GetString()!,seed.GetProperty("ObservedTicker").GetString()!),deadline.Token);var health=await client.QueryAsync("GetHealth",deadline.Token);
        if(health.Health?.State!="Running"||configuration.Count!=seed.GetProperty("ConfigurationCount").GetInt32()||configuration.Count(x=>x.Active)!=seed.GetProperty("ActiveCount").GetInt32()||points.Single().Shares!=123)throw new InvalidDataException("SCM迁移后的生产客户端回读不符。");
        File.WriteAllText(output,JsonSerializer.Serialize(new{Source="ProductionClientReadOfScmSyntheticMigration",ConfigurationCount=configuration.Count,ActiveCount=configuration.Count(x=>x.Active),Shares=points[0].Shares,Health=health.Health},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS SCM迁移后生产客户端配置和份额回读。");
    }
    public static async Task Run(string package,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("隔离目录必须不存在。");Directory.CreateDirectory(root);
        var program=Path.Combine(root,"program");CopyTree(package,program);
        if(WorkerStorageMigrationPlans.Read(program,true) is not [{PluginId:"crs.national-team",MigrationId:NationalTeamStorageMigrationEntry.MigrationId}])throw new InvalidDataException("需要显式国家队迁移候选包。");
        var data=Path.Combine(root,"data");StorageSecurity.Prepare(data,true);var master=ServiceKey.Open(data);var context=new WorkerPluginContext("crs.national-team",data,master);var key=context.OpenDataKey();
        var passed=0;void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 国家队维护管道："+text);}
        var plugin=context.DataDirectory;var dbDirectory=Path.Combine(plugin,"data");Directory.CreateDirectory(dbDirectory);var raw=new RawEvidenceStore(Path.Combine(plugin,"raw"));var original=raw.Save("synthetic-preserved-share"u8.ToArray());
        var plain=Path.Combine(root,"plain-seed");var day=new DateOnly(2026,9,1);EtfKey observed;EtfConfiguration[] configuration;
        try
        {
            using(var repository=new MarketRepository(plain,raw,approvalAuditKey:key))
            {
                var configured=repository.Configuration();observed=new(configured.First(x=>x.Active).Market,configured.First(x=>x.Active).Ticker);
                var fetch=repository.RecordFetch(original,"https://www.sse.com.cn/synthetic-migration");repository.ImportCalendar(fetch,[(observed.Market,day)],default);repository.ImportShares(fetch,[new(observed,day,123,"份")],default);
            }
            using(var db=Open(Path.Combine(plain,"market.db")))Execute(db,"UPDATE watchlist_membership SET active=0 WHERE ticker NOT IN (SELECT ticker FROM watchlist_membership ORDER BY ticker LIMIT 2)");
            using(var repository=new MarketRepository(plain,raw,approvalAuditKey:key))configuration=repository.Configuration().ToArray();
            Check(configuration.Count(x=>x.Active)==2,"准备保留两只活动ETF的合成配置池，避免默认池掩盖配置丢失");
            using(var policy=new SourceRequestPolicy(plain))policy.Failure("www.sse.com.cn",DateTimeOffset.UtcNow,403);
            Encrypt(Path.Combine(plain,"market.db"),Path.Combine(dbDirectory,"market.db"),key,2);
            Encrypt(Path.Combine(plain,"source-policy.db"),Path.Combine(dbDirectory,"source-policy.db"),key,1);
            Check(!Plain(Path.Combine(dbDirectory,"market.db"))&&!Plain(Path.Combine(dbDirectory,"source-policy.db")),"输入确为两份旧加密库，而非普通库空跑");
            using(var repository=new EncryptedTaskRepository(Path.Combine(data,"scheduler-config.db"),master))
            {
                var entry=new NationalTeamBackgroundSession(context);
                foreach(var descriptor in entry.DescribeJobs())repository.RegisterDefault("crs.national-team",descriptor with{EnabledByDefault=false});
                repository.RegisterDefault("crs.heartbeat",new("heartbeat","合成停用心跳","0 0 0 1 1 ?","China Standard Time",false));
                var plan=repository.Plans().First(x=>x.JobId=="official-share-sync");var execution=repository.Begin(plan,DateTimeOffset.UtcNow);repository.End(execution,plan,BackgroundJobResult.Success("synthetic-before-migration"));
            }
            var sourceHashes=Directory.GetFiles(dbDirectory).ToDictionary(f=>Path.GetRelativePath(plugin,f).Replace('\\','/'),Hash);var keyHash=Hash(Path.Combine(data,"service-key.dpapi"));
            var record=Snapshot(data,program);var journal=new WorkerUpgradeJournal(data);var pipe="CRS.NationalTeam.Migration."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);
            await Running(program,data,pipe,async()=>
            {
                var health=await client.QueryAsync("GetHealth");Check(health.Health?.State=="Running"&&health.Plugins?.Single(x=>x.PluginId=="crs.national-team").State=="Started","实际候选服务和生产国家队插件均启动成功");
                Check(journal.Read() is{Stage:"Healthy",StorageMigrationReceiptHash:{Length:64}},"独立迁移回执参与真实维护Healthy确认");
                Check(Plain(Path.Combine(dbDirectory,"market.db"))&&Plain(Path.Combine(dbDirectory,"source-policy.db")),"生产维护入口将两份旧库转换为普通SQLite");
                var workspace=new WorkerEtfWorkspace(client);var actual=await workspace.ConfigurationAsync(default);
                static string Config(IEnumerable<EtfConfiguration> rows)=>JsonSerializer.Serialize(rows.OrderBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Ticker,StringComparer.Ordinal));
                Check(Config(actual)==Config(configuration),"配置池的活动状态、修订及公开字段经真实客户端完整回读一致");
                Check((await workspace.PointsAsync(observed,default)).Single().Shares==123&&raw.Verify(original),"迁移前份额事实及对应原件仍可由生产查询读取");
                var history=await client.QueryAsync("GetHistory");Check(history.History?.Any(x=>x.Checkpoint=="synthetic-before-migration")==true,"Worker旧执行记录也保留");
                Check(!Plain(Path.Combine(data,"scheduler-config.db"))&&Hash(Path.Combine(data,"service-key.dpapi"))==keyHash,"Worker管理库保持加密，机器密钥文件摘要不变");
                using var policy=new SourceRequestPolicy(dbDirectory);Check(policy.Read("www.sse.com.cn").Failures==1&&policy.Read("www.sse.com.cn").Blocked>DateTimeOffset.UtcNow.AddHours(5),"来源拒绝计数与冷却时间不因转换清零");
            });
            Check(sourceHashes.All(x=>Hash(Path.Combine(plugin,"storage-migrations",record.Id,"original",x.Key))==x.Value&&Hash(Path.Combine(data,"maintenance",record.Id,"snapshot","plugins/crs.national-team",x.Key))==x.Value),"旧加密资料归档及全量快照逐文件摘要保持一致");
            // 实际生产插件已经打开过新库；显式重试仍沿用原代次，不用旧快照覆写。
            File.WriteAllBytes(Path.Combine(data,"maintenance/upgrade.json"),JsonSerializer.SerializeToUtf8Bytes(journal.Read()! with{Stage="Switched"}));
            await Running(program,data,pipe,async()=>{Check((await new WorkerEtfWorkspace(client).PointsAsync(observed,default)).Single().Shares==123&&journal.Read()!.Stage=="Healthy","生产插件打开新库后，同一维护代次仍可显式重试并回读");});
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="IsolatedUnmodifiedCandidateProductionNationalTeamMigrationAndClient",FormalLocalServiceMigrationVerified=false,record.Id,ConfigurationCount=configuration.Length,ActiveCount=configuration.Count(x=>x.Active),KeyPreserved=true},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{CryptographicOperations.ZeroMemory(master);CryptographicOperations.ZeroMemory(key);}
    }
    private static WorkerUpgradeRecord Snapshot(string data,string program)
    {
        var files=Directory.GetFiles(data,"*",SearchOption.AllDirectories);var id=Guid.NewGuid().ToString("N");var root=Path.Combine(data,"maintenance",id);var snapshot=Path.Combine(root,"snapshot");var hashes=new Dictionary<string,string>();
        foreach(var file in files){var relative=Path.GetRelativePath(data,file).Replace('\\','/');var target=Path.Combine(snapshot,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);hashes.Add(relative,Hash(file));}
        var manifest=Path.Combine(root,"snapshot.json");File.WriteAllText(manifest,JsonSerializer.Serialize(new{Format=1,ReleaseHash=new string('A',64),Files=hashes}));
        var record=new WorkerUpgradeRecord(1,id,new string('A',64),WorkerUpgradeJournal.ReleaseHash(program),"2.0.0","2.0.0","Switched",false,DateTimeOffset.UtcNow,Hash(manifest));File.WriteAllBytes(Path.Combine(data,"maintenance/upgrade.json"),JsonSerializer.SerializeToUtf8Bytes(record));return record;
    }
    private static async Task Running(string program,string data,string pipe,Func<Task> check)
    {
        var info=new ProcessStartInfo(Path.Combine(program,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var value in new[]{"--console","--data",data,"--pipe",pipe})info.ArgumentList.Add(value);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var client=new BackgroundPipeClient(pipe);
            while(true){timeout.Token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("国家队候选提前退出。");try{if((await client.QueryAsync("GetHealth",timeout.Token)).Health?.State=="Running")break;}catch(BackgroundServiceException error) when(error.Code=="ServiceUnavailable"){}await Task.Delay(100,timeout.Token);}
            await check().WaitAsync(timeout.Token);
        }
        finally{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();var suffix=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(data,"stdout-"+suffix+".log"),await stdout);File.WriteAllText(Path.Combine(data,"stderr-"+suffix+".log"),await stderr);}
    }
    private static void CopyTree(string source,string target)
    {foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}}
    private static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    private static bool Plain(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);Span<byte> bytes=stackalloc byte[16];return file.Read(bytes)==16&&bytes.SequenceEqual("SQLite format 3\0"u8);}
    private static SqliteConnection Open(string path){SQLitePCL.Batteries_V2.Init();var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();return db;}
    private static void Execute(SqliteConnection db,string sql){using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void Encrypt(string source,string target,byte[] key,int version)
    {
        using var db=Open(source);using var command=db.CreateCommand();command.CommandText="ATTACH DATABASE $target AS legacy KEY '"+Convert.ToHexString(key)+"'";command.Parameters.AddWithValue("$target",target);command.ExecuteNonQuery();command.CommandText="SELECT sqlcipher_export('legacy')";command.ExecuteScalar();Execute(db,"PRAGMA legacy.user_version="+version+"; DETACH DATABASE legacy;");
    }
}
