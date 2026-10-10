using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;
using Microsoft.Data.Sqlite;

internal static class VaultChecks
{
    /// <summary>实际原生 SQLCipher 验证静态文件、WAL、一致性备份、跨目录恢复、改密和锁定旧会话。</summary>
    public static async Task RunAsync(string root)
    {
        const string password="Synthetic-Vault-Password-2026",newPassword="Changed-Vault-Password-2026",sentinel="PRIVATE_ACCOUNT_SENTINEL";
        var directory=Path.Combine(root,"secure-vault"); using var vault=new VaultManager();
        var created=await vault.CreateAsync(directory,password);
        await TestStores.Enroll(vault);
        var source=new CalculationResult {Year=2025,SnapshotId="SECURITY-FACT",Summary=new(100,0,0,20,0,0,20),CoveredAccounts=[sentinel]};
        vault.Repository.Save(source); vault.Repository.AddReview(source.SnapshotId,"合成依据",sentinel);
        var originalRepository=vault.Repository;
        var oldOperations=new DesktopOperations(originalRepository,root,vault);
        var database=Path.Combine(directory,"crs.db");
        if(Encoding.UTF8.GetString(File.ReadAllBytes(database)).Contains(sentinel) || Encoding.ASCII.GetString(File.ReadAllBytes(database),0,16)=="SQLite format 3\0") throw new Exception("业务库未加密。");
        using(var ordinary=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=database,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString()))
        {
            ordinary.Open(); try {ordinary.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master"); throw new Exception("无密钥连接能读取业务库。");} catch(SqliteException){}
        }
        var metadata=File.ReadAllText(Path.Combine(directory,"vault.json"));
        if(metadata.Contains(password) || metadata.Contains(created.RecoveryKey)) throw new Exception("封装元数据泄露凭证。");
        var backup=Path.Combine(root,"secure.crsbak");
        using(var writer=vault.OpenForTest())
        {
            if(string.IsNullOrWhiteSpace(writer.ExecuteScalar<string>("PRAGMA cipher_version"))) throw new Exception("实际原生库不是 SQLCipher。");
            Console.WriteLine("SQLCipher 原生版本："+writer.ExecuteScalar<string>("PRAGMA cipher_version"));
            if(writer.ExecuteScalar<string>("PRAGMA journal_mode=WAL")!="wal") throw new Exception("原生提供器未启用 WAL。");
            // 固定一个旧读取快照，确保新提交仍在 WAL 中，不能靠自动 checkpoint 冒充 WAL 备份测试。
            using var reader=writer.BeginTransaction(deferred:true); writer.ExecuteScalar<int>("SELECT COUNT(*) FROM reviews",transaction:reader);
            vault.Repository.AddReview(source.SnapshotId,"WAL 合成",sentinel+"_WAL");
            var wal=database+"-wal";
            if(!File.Exists(wal)) throw new Exception("WAL 样本没有形成已提交的待合入记录。");
            using(var walStream=new FileStream(wal,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var walBytes=new MemoryStream()) {walStream.CopyTo(walBytes); if(Encoding.UTF8.GetString(walBytes.ToArray()).Contains(sentinel)) throw new Exception("WAL 泄露明文。");}
            await vault.BackupAsync(backup);
        }
        if(Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains(sentinel)) throw new Exception("备份泄露业务内容。");
        var backupBefore=File.ReadAllBytes(backup);
        await Reject(()=>vault.BackupAsync(backup)); if(!backupBefore.SequenceEqual(File.ReadAllBytes(backup))) throw new Exception("覆盖已存在备份。");
        var changedTime=vault.Clock()+30; vault.Clock=()=>changedTime;
        await Reject(()=>vault.ChangePasswordAsync("wrong-password",newPassword,vault.CodeForTest())); await vault.ChangePasswordAsync(password,newPassword,vault.CodeForTest());
        vault.Lock(); await Reject(()=>vault.UnlockAsync(directory,password)); await Task.Delay(1100); await vault.UnlockAsync(directory,newPassword);
        await TestStores.Authenticate(vault);
        try {originalRepository.Save(source); throw new Exception("旧会话借新解锁恢复写入。");} catch(CrsException){}
        var forbidden=Path.Combine(root,"old-session-export.xlsx");
        try {oldOperations.Reports.Export(forbidden,source,[]); throw new Exception("旧导出借新会话执行。");} catch(CrsException){}
        if(File.Exists(forbidden)) throw new Exception("锁定的旧导出留下文件。");
        vault.Lock(); await Reject(()=>vault.ResetPasswordAsync(directory,new string('0',64),password));
        await vault.ResetPasswordAsync(directory,created.RecoveryKey,password); await TestStores.Enroll(vault);
        if(vault.Repository.Load(source.SnapshotId).Summary!=source.Summary) throw new Exception("改密或恢复改变了冻结金额。"); vault.Lock();
        using var portable=new VaultManager(); var restored=Path.Combine(root,"restored-other-device");
        await portable.RestoreAsync(backup,restored,created.RecoveryKey,true,newPassword); await portable.UnlockAsync(restored,newPassword);
        await TestStores.Authenticate(portable);
        if(portable.Repository.Load(source.SnapshotId).CoveredAccounts.Single()!=sentinel || portable.Repository.Reviews(source.SnapshotId).Count!=2) throw new Exception("备份未恢复一致的业务和 WAL 记录。"); portable.Lock();
        var passwordRestored=Path.Combine(root,"restored-password"); await portable.RestoreAsync(backup,passwordRestored,password,false,newPassword);
        await Reject(()=>portable.RestoreAsync(backup,restored,password,false,newPassword));
        await Reject(()=>portable.RestoreAsync(backup,Path.Combine(root,"wrong-secret"),"wrong-password",false,newPassword));
        var tampered=JsonNode.Parse(backupBefore)!.AsObject(); var data=Convert.FromBase64String(tampered["Payload"]!["Ciphertext"]!.GetValue<string>()); data[0]^=1;
        tampered["Payload"]!["Ciphertext"]=Convert.ToBase64String(data); var invalid=Path.Combine(root,"tampered.crsbak"); File.WriteAllText(invalid,tampered.ToJsonString());
        var invalidTarget=Path.Combine(root,"must-not-exist"); await Reject(()=>portable.RestoreAsync(invalid,invalidTarget,password,false,newPassword));
        if(Directory.Exists(invalidTarget)) throw new Exception("损坏备份留下目标目录。");
        // 只构造明文头样本；应用不得建立明文连接、读表、重命名或删除旧文件。
        var legacy=Path.Combine(root,"legacy-plaintext"); Directory.CreateDirectory(legacy); var legacyDb=Path.Combine(legacy,"crs.db");
        File.WriteAllBytes(legacyDb,Encoding.ASCII.GetBytes("SQLite format 3\0DO_NOT_TOUCH")); File.WriteAllText(legacyDb+"-wal","old-wal");
        var before=File.ReadAllBytes(legacyDb);
        if(portable.Inspect(legacy)!=VaultState.PlaintextRejected) throw new Exception("明文头未被拒绝。");
        await Reject(()=>portable.UnlockAsync(legacy,password)); await Reject(()=>portable.CreateAsync(legacy,password));
        if(!before.SequenceEqual(File.ReadAllBytes(legacyDb)) || File.ReadAllText(legacyDb+"-wal")!="old-wal") throw new Exception("拒绝明文库时改动了旧资料。");
        Console.WriteLine("保险库集成验证通过：实际 SQLCipher、无密钥拒绝、元数据不泄密、WAL 加密及一致性备份、覆盖拒绝、改密／恢复、旧会话阻断、两种备份恢复、篡改拒绝、明文旧库保持原样（14 项）。");
    }
    private static async Task Reject(Func<Task> action)
    {
        try {await action();} catch(Exception ex) when(ex is CrsException or System.Security.Cryptography.CryptographicException or IOException or JsonException) {return;}
        throw new Exception("预期拒绝的安全操作被接受。");
    }
}
