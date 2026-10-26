using System.Security.Cryptography;
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
        using(var source=Open(path))
        {
            using var attach=source.CreateCommand();attach.CommandText="ATTACH DATABASE $target AS legacy KEY '"+Convert.ToHexString(TestMarketData.Key)+"'";attach.Parameters.AddWithValue("$target",legacy);attach.ExecuteNonQuery();
            _=Scalar(source,"SELECT sqlcipher_export('legacy')");Execute(source,"PRAGMA legacy.user_version=2; DETACH DATABASE legacy;");
        }
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

        var futureFolder=Path.Combine(root,"future");Directory.CreateDirectory(futureFolder);var future=Path.Combine(futureFolder,"market.db");
        using(var db=Open(future))Execute(db,"CREATE TABLE preserve(value TEXT); INSERT INTO preserve VALUES('keep'); PRAGMA user_version=99;");
        Reject(()=>{using var invalid=TestMarketData.Open(futureFolder,raw);},"未来Schema拒绝初始化");
        using(var db=Open(future))Check(Convert.ToInt32(Scalar(db,"PRAGMA user_version"))==99&&Scalar(db,"SELECT value FROM preserve") as string=="keep","初始化失败不修改版本或已有数据");
        using(var exclusive=new FileStream(future,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(exclusive.Length>0,"构造失败也释放数据库连接");
        Console.WriteLine($"PASS 公开研究存储合计{passed}；只使用合成数据库，未迁移已安装Worker或修改收入/报税库。");
    }
    private static SqliteConnection Open(string path)
    {SQLitePCL.Batteries_V2.Init();var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();return db;}
    private static object? Scalar(SqliteConnection db,string sql)
    {using var command=db.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static void Execute(SqliteConnection db,string sql)
    {using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
}
