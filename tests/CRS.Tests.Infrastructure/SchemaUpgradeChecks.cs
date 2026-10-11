using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using CRS.Security.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

internal static class SchemaUpgradeChecks
{
    /// <summary>所有故障与旧库均在隔离保险库中构造，验证备份先行、事务回滚、程序归档和不同保险库隔离。</summary>
    internal static async Task RunAsync(string root)
    {
        const string password="Synthetic-Schema-Password-2026";
        var directory=Path.Combine(root,"schema-upgrade");
        using var vault=new VaultManager(); await vault.CreateAsync(directory,password); await TestStores.Enroll(vault);
        var legacy=new CalculationResult {Year=2025,SnapshotId="LEGACY",Summary=new(100,0,0,20,0,0,20),CoveredAccounts=["PRIVATE-SCHEMA-SENTINEL"]};
        var original=JsonSerializer.Serialize(legacy);
        using var key=VaultKeyManager.Unlock(VaultFiles.ReadEnvelope(directory),password);
        using var session=new VaultSession(directory,key.Clone());
        using(var c=session.Open())
        {
            if(c.ExecuteScalar<int>("PRAGMA user_version")!=2) throw new Exception("新建库未使用新版结构。");
            // 模拟真正的旧版业务结构，而不是只改版本数字；认证表保持原样。
            c.Execute("""
                DROP TABLE run_execution_artifacts; DROP TABLE execution_artifacts; DROP TABLE reviews; DROP TABLE calculations;
                CREATE TABLE calculations(id TEXT PRIMARY KEY,year INTEGER NOT NULL,created_utc TEXT NOT NULL,
                  complete INTEGER NOT NULL,issues INTEGER NOT NULL,result_json TEXT NOT NULL,carry BLOB);
                CREATE TABLE reviews(id INTEGER PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES calculations(id),
                  category TEXT NOT NULL,evidence TEXT NOT NULL,created_utc TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            c.Execute("INSERT INTO calculations VALUES('LEGACY',2025,'synthetic',0,0,@Json,NULL)",new {Json=original});
            c.Execute("INSERT INTO reviews(snapshot_id,category,evidence,created_utc) VALUES('LEGACY','合成依据','synthetic','synthetic')");
            // 备份失败必须在任何 DDL 前中止，旧版列、版本和记录均不变。
            Reject<IOException>(()=>DatabaseSchema.Ensure(session,c,writeBackup:(_,_,_)=>throw new IOException("模拟备份磁盘满。")));
            AssertOld(c);
            // 已完成 DDL 和版本写入后故障也必须回滚，升级前备份则保留。
            Reject<IOException>(()=>DatabaseSchema.Ensure(session,c,afterChanges:()=>throw new IOException("模拟提交前故障。")));
            AssertOld(c);
        }
        var backup=Directory.GetFiles(directory,"schema-v1-*.crsbak").Single();
        var text=File.ReadAllText(backup);
        if(text.Contains("PRIVATE-SCHEMA-SENTINEL") || text.Contains(password)) throw new Exception("升级备份含明文资料或口令。");
        // 确认升级前备份确为旧业务版本，不能在修改之后再补做快照。
        var envelope=JsonSerializer.Deserialize<VaultBackupFile>(text,VaultFiles.Options)!;
        var plain=CRS.Security.Cryptography.AuthenticatedEncryption.Open(envelope.Payload,key);
        VaultBackupBody body;
        try {body=JsonSerializer.Deserialize<VaultBackupBody>(plain,VaultFiles.Options)!;} finally {CryptographicOperations.ZeroMemory(plain);}
        var snapshot=Path.Combine(root,"pre-upgrade-snapshot.db"); File.WriteAllBytes(snapshot,body.Database); CryptographicOperations.ZeroMemory(body.Database);
        using(var c=session.Open(snapshot)) AssertOld(c);
        using(var c=session.Open())
        {
            DatabaseSchema.Ensure(session,c);
            if(c.ExecuteScalar<int>("PRAGMA user_version")!=2 || c.ExecuteScalar<string>("SELECT result_json FROM calculations WHERE id='LEGACY'")!=original
                || c.ExecuteScalar<int>("SELECT execution_archived FROM calculations WHERE id='LEGACY'")!=0
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM execution_artifacts")!=0) throw new Exception("升级改写旧结果或补造程序归档。");
            var count=Directory.GetFiles(directory,"schema-v1-*.crsbak").Length; DatabaseSchema.Ensure(session,c);
            if(Directory.GetFiles(directory,"schema-v1-*.crsbak").Length!=count) throw new Exception("新版库重复执行升级或备份。");
        }
        if(vault.Repository.Load("LEGACY").Summary!=legacy.Summary || vault.Repository.Reviews("LEGACY").Count!=1) throw new Exception("升级丢失旧结果或复核。");
        var first=Result("NEW-ONE"); var second=Result("NEW-TWO"); vault.Repository.Save(first); vault.Repository.Save(second);
        using(var c=session.Open())
        {
            if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM execution_artifacts")!=3 || c.ExecuteScalar<int>("SELECT COUNT(*) FROM run_execution_artifacts")!=6)
                throw new Exception("程序归档没有按摘要去重并保留每次运行关系。");
            var saved=c.QuerySingle<byte[]>("SELECT content FROM execution_artifacts WHERE assembly_name='CRS.Domain'");
            c.Execute("UPDATE execution_artifacts SET content=zeroblob(byte_length) WHERE assembly_name='CRS.Domain'");
            Reject<CrsException>(()=>vault.Repository.Load(first.SnapshotId));
            Reject<CrsException>(()=>vault.Repository.Save(Result("MUST-NOT-SAVE")));
            if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id='MUST-NOT-SAVE'")!=0) throw new Exception("归档失败留下半成品任务。");
            c.Execute("UPDATE execution_artifacts SET content=@Content WHERE assembly_name='CRS.Domain'",new {Content=saved});
            c.Execute("DELETE FROM run_execution_artifacts WHERE snapshot_id='NEW-ONE' AND role='Domain'");
            Reject<CrsException>(()=>vault.Repository.Load(first.SnapshotId));
            c.Execute("INSERT INTO run_execution_artifacts SELECT 'NEW-ONE','Domain',sha256 FROM execution_artifacts WHERE assembly_name='CRS.Domain'");
            // 高版本及伪装旧版本都不得被降级或自动修补。
            var count=Directory.GetFiles(directory,"schema-v1-*.crsbak").Length;
            c.Execute("PRAGMA user_version=99"); Reject<CrsException>(()=>DatabaseSchema.Ensure(session,c));
            if(c.ExecuteScalar<int>("PRAGMA user_version")!=99) throw new Exception("新版本被降级。");
            c.Execute("PRAGMA user_version=1"); Reject<CrsException>(()=>DatabaseSchema.Ensure(session,c));
            if(Directory.GetFiles(directory,"schema-v1-*.crsbak").Length!=count) throw new Exception("未识别结构仍执行升级备份。");
            c.Execute("PRAGMA user_version=2");
        }
        // 旧版升级前备份与新版含程序文件备份都通过现有便携恢复路径。
        using var restored=new VaultManager(); var oldTarget=Path.Combine(root,"restored-schema-old");
        await restored.RestoreAsync(backup,oldTarget,password,false,password); await restored.UnlockAsync(oldTarget,password); await TestStores.Authenticate(restored);
        if(restored.Repository.Load("LEGACY").Summary!=legacy.Summary) throw new Exception("升级前备份无法恢复。"); restored.Lock();
        var currentBackup=Path.Combine(root,"schema-current.crsbak"); await vault.BackupAsync(currentBackup);
        var newTarget=Path.Combine(root,"restored-schema-current"); await restored.RestoreAsync(currentBackup,newTarget,password,false,password);
        await restored.UnlockAsync(newTarget,password); await TestStores.Authenticate(restored);
        if(restored.Repository.Load("NEW-ONE").Summary!=first.Summary || restored.Repository.Load("LEGACY").Summary!=legacy.Summary) throw new Exception("含程序归档的备份无法恢复。");
        await Isolation(root,password,directory,key);
        Console.WriteLine("升级与程序归档通过：备份失败／提交前失败回滚、旧事实保留、重复升级拒绝、未知结构／高版本拒绝、归档去重／损坏／缺失／事务拒绝、两种结构备份恢复及保险库密钥隔离。");

        void AssertOld(SqliteConnection c)
        {
            if(c.ExecuteScalar<int>("PRAGMA user_version")!=1 || c.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('calculations')")!=7
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='execution_artifacts'")!=0
                || c.ExecuteScalar<string>("SELECT result_json FROM calculations WHERE id='LEGACY'")!=original
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM reviews")!=1) throw new Exception("升级故障没有回滚旧资料和结构。");
        }
    }
    private static async Task Isolation(string root,string password,string original,CRS.Security.Cryptography.SecretKey originalKey)
    {
        var directory=Path.Combine(root,"independent-schema-vault"); using var other=new VaultManager(); await other.CreateAsync(directory,password); await TestStores.Enroll(other);
        using var otherKey=VaultKeyManager.Unlock(VaultFiles.ReadEnvelope(directory),password);
        var left=originalKey.Copy(); var right=otherKey.Copy();
        try {if(left.SequenceEqual(right)) throw new Exception("不同保险库复用了数据库密钥。");}
        finally {CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right);}
        var path=Path.Combine(original,"crs.db"); var before=SHA256.HashData(File.ReadAllBytes(path));
        using(var wrong=new VaultSession(original,otherKey.Clone())) Reject<CrsException>(wrong.Verify);
        if(!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new Exception("串库拒绝改写了原数据库。");
    }
    private static CalculationResult Result(string id)=>new() {Year=2025,SnapshotId=id,Summary=new(1,0,0,0,0,0,0)};
    private static void Reject<T>(Action action) where T:Exception {try {action();} catch(T) {return;} throw new Exception("升级未拒绝："+typeof(T).Name);}
}
