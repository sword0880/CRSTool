using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>只在隔离目录验证普通SQLite、连接生命周期和旧加密资料的保留式离线导出。</summary>
internal static class PublicResearchStorageChecks
{
    public static void Run(string root)
    {
        var passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS 公开研究存储："+name);}
        void Reject(Action action,string name)
        {try{action();}catch(Exception e) when(e is InvalidDataException or SqliteException or IOException){Check(true,name);return;}throw new Exception("未拒绝："+name);}
        bool Plain(string path)
        {using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);Span<byte> bytes=stackalloc byte[16];return stream.Read(bytes)==16&&bytes.SequenceEqual("SQLite format 3\0"u8);}
        string HashFile(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
        var data=Path.Combine(root,"current");var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);
        var path=Path.Combine(data,"market.db");var day=new DateOnly(2026,9,1);var key=new EtfKey("SSE","510300");
        var original=raw.Save("public SQLite share evidence"u8.ToArray());
        using(var repository=TestMarketData.Open(data,raw))
        {
            var fetch=repository.RecordFetch(original,"https://www.sse.com.cn/synthetic-plain");
            repository.ImportCalendar(fetch,[("SSE",day)],default);repository.ImportShares(fetch,[new(key,day,123,"份")],default);
            Check(Plain(path),"市场库具有普通SQLite文件头");
            using(var direct=Open(path))Check(Convert.ToInt32(Scalar(direct,"SELECT count(*) FROM security_master"))>=24,"不提供密钥也能读取国家队公开目录");
            var snapshot=Path.Combine(root,"snapshot.db");repository.Snapshot(snapshot);
            Check(Plain(snapshot),"独立快照也不加密");
            using(var direct=Open(snapshot))Check(Convert.ToString(Scalar(direct,"SELECT total_shares FROM share_observation_revision"))=="123","普通SQLite快照保留金融事实");
            var reader=(IMarketReadSessions)repository;
            Check(reader.Read(current=>current.Points(key).Single().Shares,default)==123,"独立只读会话读取同一正式事实");
            try{reader.Read(current=>{current.RecordSyncCursor(day);return true;},default);throw new Exception("只读会话允许写入");}
            catch(InvalidOperationException){Check(true,"改为普通SQLite后只读快照仍禁止写入");}
        }
        using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(exclusive.Length>0,"仓储释放后连接和文件句柄完全归还");
        Check(raw.Verify(original),"关闭数据库不销毁调用方拥有的原件库");
        using(var policy=new SourceRequestPolicy(data))policy.Failure("www.sse.com.cn",DateTimeOffset.UtcNow,403);
        Check(Plain(Path.Combine(data,"source-policy.db")),"来源退避库使用普通SQLite");
        using(var policy=new SourceRequestPolicy(data))Check(policy.Read("www.sse.com.cn").Blocked>DateTimeOffset.UtcNow.AddHours(5.9),"来源退避重启后保留，未退回内存或清零");

        var legacyFolder=Path.Combine(root,"legacy");Directory.CreateDirectory(legacyFolder);
        var legacy=Path.Combine(legacyFolder,"market.db");
        EncryptFixture(path,legacy,2);
        Check(!Plain(legacy),"旧加密夹具确实不是普通SQLite文件");var before=HashFile(legacy);
        Reject(()=>{using var blocked=TestMarketData.Open(legacyFolder,raw);},"正常启动拒绝把旧加密文件当空库重建");
        Check(HashFile(legacy)==before,"拒绝启动后旧加密原文件逐字节保留");
        var migratedFolder=Path.Combine(root,"migrated");var migrated=Path.Combine(migratedFolder,"market.db");
        var result=LegacyNationalTeamExport.Export(legacy,migrated,TestMarketData.Key);
        Check(Plain(migrated)&&result is {DatabaseKind:"Market",SchemaVersion:2}&&result.Rows>0,"显式离线导出生成可直接读取的新普通SQLite");
        Check(HashFile(legacy)==before,"离线导出没有修改旧库字节");
        using(var reopened=TestMarketData.Open(migratedFolder,raw))Check(reopened.Points(key).Single().Shares==123&&reopened.Configuration().Count>=24,"新库重开保留目录、份额和原件身份");
        var existing=HashFile(migrated);Reject(()=>LegacyNationalTeamExport.Export(legacy,migrated,TestMarketData.Key),"已有目标不允许覆盖");
        Check(HashFile(migrated)==existing&&HashFile(legacy)==before,"拒绝覆盖不修改新旧任一资料库");
        var wrong=Path.Combine(root,"wrong","market.db");Reject(()=>LegacyNationalTeamExport.Export(legacy,wrong,new byte[32]),"错误旧密钥拒绝导出");
        Check(!File.Exists(wrong)&&HashFile(legacy)==before,"错误密钥不留下假成功目标或改变原文件");
        using(var lockFile=new FileStream(legacy,FileMode.Open,FileAccess.Read,FileShare.Read))
            Reject(()=>LegacyNationalTeamExport.Export(legacy,Path.Combine(root,"busy","market.db"),TestMarketData.Key),"原库被占用时拒绝迁移");
        Reject(()=>LegacyNationalTeamExport.Export(path,Path.Combine(root,"unnecessary.db"),TestMarketData.Key),"普通SQLite不重复走旧加密迁移");

        var oldPolicy=Path.Combine(root,"legacy-policy","source-policy.db");Directory.CreateDirectory(Path.GetDirectoryName(oldPolicy)!);
        EncryptFixture(Path.Combine(data,"source-policy.db"),oldPolicy,1);
        var policyOutput=Path.Combine(root,"migrated-policy","source-policy.db");
        var policyResult=LegacyNationalTeamExport.Export(oldPolicy,policyOutput,TestMarketData.Key);
        using(var policy=new SourceRequestPolicy(Path.GetDirectoryName(policyOutput)!))
            Check(policyResult.DatabaseKind=="SourcePolicy"&&policy.Read("www.sse.com.cn").Blocked>DateTimeOffset.UtcNow.AddHours(5.9),"来源策略离线导出保留原有拒绝期限");
        var walFolder=Path.Combine(root,"wal-legacy");CreateCrashedWal(walFolder);
        var walDatabase=Path.Combine(walFolder,"source-policy.db");var walBefore=HashFile(walDatabase+"-wal");var databaseBefore=HashFile(walDatabase);
        var walOutput=Path.Combine(root,"migrated-wal","source-policy.db");
        _=LegacyNationalTeamExport.Export(walDatabase,walOutput,TestMarketData.Key);
        using(var policy=new SourceRequestPolicy(Path.GetDirectoryName(walOutput)!))
            Check(policy.Read("www.sse.com.cn").Failures==7&&policy.Read("www.sse.com.cn").Blocked.Year==2030,"进程崩溃后仅在WAL中的已提交状态也完整导出");
        Check(HashFile(walDatabase)==databaseBefore&&HashFile(walDatabase+"-wal")==walBefore,"恢复只发生在副本，原主库与WAL字节均保留");
        Check(!Directory.EnumerateFiles(root,"*.preparing-*",SearchOption.AllDirectories).Any(),"成功或失败的导出不遗留临时解密库");

        var futureFolder=Path.Combine(root,"future");Directory.CreateDirectory(futureFolder);var future=Path.Combine(futureFolder,"market.db");
        using(var db=Open(future))Execute(db,"CREATE TABLE preserve(value TEXT); INSERT INTO preserve VALUES('keep'); PRAGMA user_version=99;");
        Reject(()=>{using var invalid=TestMarketData.Open(futureFolder,raw);},"未来Schema拒绝初始化");
        using(var db=Open(future))Check(Convert.ToInt32(Scalar(db,"PRAGMA user_version"))==99&&Scalar(db,"SELECT value FROM preserve") as string=="keep","初始化失败不修改版本或已有数据");
        using(var exclusive=new FileStream(future,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(exclusive.Length>0,"构造失败也释放数据库连接");
        var emptyFolder=Path.Combine(root,"truncated");Directory.CreateDirectory(emptyFolder);
        var emptyMarket=Path.Combine(emptyFolder,"market.db");File.WriteAllBytes(emptyMarket,[]);
        Reject(()=>{using var truncated=TestMarketData.Open(emptyFolder,raw);},"已有零字节市场库拒绝当作新库初始化");
        Check(new FileInfo(emptyMarket).Length==0,"拒绝后保留被截断文件，不掩盖原故障");
        var emptyPolicy=Path.Combine(emptyFolder,"source-policy.db");File.WriteAllBytes(emptyPolicy,[]);
        Reject(()=>{using var truncated=new SourceRequestPolicy(emptyFolder);},"已有零字节策略库不能重建成空退避状态");
        Check(new FileInfo(emptyPolicy).Length==0,"策略库截断后不伪造来源恢复");
        Console.WriteLine($"PASS 公开研究存储合计{passed}；只使用合成数据库，未迁移已安装Worker或修改收入/报税库。");
    }
    private static SqliteConnection Open(string path)
    {SQLitePCL.Batteries_V2.Init();var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();return db;}
    private static object? Scalar(SqliteConnection db,string sql)
    {using var command=db.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static void Execute(SqliteConnection db,string sql)
    {using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void EncryptFixture(string sourcePath,string target,int version)
    {
        using var source=Open(sourcePath);using var attach=source.CreateCommand();
        attach.CommandText="ATTACH DATABASE $target AS legacy KEY '"+Convert.ToHexString(TestMarketData.Key)+"'";attach.Parameters.AddWithValue("$target",target);attach.ExecuteNonQuery();
        _=Scalar(source,"SELECT sqlcipher_export('legacy')");Execute(source,"PRAGMA legacy.user_version="+version+"; DETACH DATABASE legacy;");
    }
    private static void CreateCrashedWal(string folder)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(PublicResearchStorageChecks).Assembly.Location);
        start.ArgumentList.Add("--public-storage-wal-fixture");start.ArgumentList.Add(folder);
        using var child=Process.Start(start)??throw new Exception("无法启动隔离WAL夹具。");
        try
        {
            var watch=Stopwatch.StartNew();var ready=Path.Combine(folder,"ready");
            while(!File.Exists(ready))
            {if(child.HasExited)throw new Exception("WAL夹具提前结束："+child.StandardError.ReadToEnd());if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("WAL夹具未准备完成。");Thread.Sleep(20);}
            child.Kill(entireProcessTree:true);if(!child.WaitForExit(10000))throw new TimeoutException("WAL夹具未退出。");
        }
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);child.WaitForExit(10000);}}
    }
    public static void WalFixture(string folder)
    {
        var resolved=Path.GetFullPath(folder);var boundary=Path.Combine(Path.GetTempPath(),"CRS.PublicResearchStorage")+Path.DirectorySeparatorChar;
        if(!resolved.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(resolved)!="wal-legacy"||Directory.Exists(resolved))throw new InvalidDataException("WAL夹具只能写入本次独立临时目录。");
        Directory.CreateDirectory(resolved);SQLitePCL.Batteries_V2.Init();
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(resolved,"source-policy.db"),Password=Convert.ToHexString(TestMarketData.Key),Pooling=false}.ToString());db.Open();
        Execute(db,"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE source_policy(source TEXT PRIMARY KEY,next_utc TEXT NOT NULL,blocked_utc TEXT NOT NULL,failures INTEGER NOT NULL); PRAGMA user_version=1; INSERT INTO source_policy VALUES('SSE','2030-01-01T00:00:00.0000000+00:00','2030-01-02T00:00:00.0000000+00:00',7);");
        File.WriteAllText(Path.Combine(resolved,"ready"),"SyntheticWalCommitted");Thread.Sleep(Timeout.Infinite);
    }
}
