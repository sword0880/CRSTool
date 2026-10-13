using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using Dapper;

internal static class ResourceProtectionChecks
{
    /// <summary>隔离加密库验证读取前拒绝、分块攻击及超过旧上限的真实备份恢复。</summary>
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        const string password="Synthetic-Resource-Password-2026";
        var directory=Path.Combine(root,"resource-vault");
        using var vault=new VaultManager(); var creation=await vault.CreateAsync(directory,password); await TestStores.Enroll(vault);
        var store=(LocalStore)vault.Repository;
        store.Save(new CalculationResult {Year=2025,SnapshotId="RESOURCE",Summary=new(1,0,0,0,0,0,0)});
        // 旧历史仍可读；坏文本、重复字段和过深结构不能进入业务模型。
        foreach(var json in new[] {"{} {}", "{\"Year\":2025,\"Year\":2024}", "{\"a\":"+new string('[',33)+"0"+new string(']',33)+"}"})
            Reject(()=>PersistedResultLimits.Validate(Encoding.UTF8.GetBytes(json)));
        var tooMany="{\"Warnings\":["+string.Join(',',Enumerable.Repeat("0",200001))+"]}";
        Reject(()=>PersistedResultLimits.Validate(Encoding.UTF8.GetBytes(tooMany)));
        var oversizedText=new CalculationResult {Year=2025,SnapshotId="REJECT",Summary=new(0,0,0,0,0,0,0),Warnings=[new string('x',32769)]};
        Reject(()=>store.Save(oversizedText));
        if(store.History().Count!=1) throw new Exception("超限保存留下半个任务。");
        var carryPath=Path.Combine(root,"oversized-carry.json");
        using(var file=File.Create(carryPath)) file.SetLength(2*1024*1024+1);
        Reject(()=>CarryFiles.Read(carryPath));

        using var key=VaultKeyManager.Unlock(VaultFiles.ReadEnvelope(directory),password);
        ChunkAttacks(root,VaultFiles.ReadEnvelope(directory),key);
        // 使用 SQLCipher 写入真实的大记录，验证拒绝前未把整条正文分配到托管内存。
        using(var c=vault.OpenForTest())
            c.Execute("INSERT INTO calculations VALUES('OVERSIZED',2025,'synthetic',0,0,zeroblob(@Bytes),NULL,0)",new {Bytes=PersistedResultLimits.MaxBytes+1});
        var allocated=GC.GetAllocatedBytesForCurrentThread(); Reject(()=>store.Load("OVERSIZED"));
        if(GC.GetAllocatedBytesForCurrentThread()-allocated>4*1024*1024) throw new Exception("读取前拒绝仍分配了过大的历史正文。");
        if(store.History().Count!=2 || store.History().Single(h=>h.Id=="OVERSIZED").Broker!="记录异常")
            throw new Exception("超大记录导致整页历史不可用或被静默隐藏。");
        var capacity=vault.BackupCapacity!;
        if(capacity.DatabaseBytes<=128*1024*1024 || capacity.MaximumBytes!=ChunkedVaultBackup.MaxDatabaseBytes)
            throw new Exception("容量未包含真实逻辑数据库页。");
        var backup=Path.Combine(root,"large.crsbak"); await vault.BackupAsync(backup); vault.Lock();
        var target=Path.Combine(root,"large-restored");
        using var restored=new VaultManager(); await restored.RestoreAsync(backup,target,creation.RecoveryKey,true,password);
        await restored.UnlockAsync(target,password); await TestStores.Authenticate(restored);
        if(restored.Repository.Load("RESOURCE").Summary.DividendCny!=1) throw new Exception("大库恢复丢失有效业务记录。");
        Reject(()=>restored.Repository.Load("OVERSIZED")); restored.Lock();
        // 旧版加密 JSON 只保留恢复能力，不能与禁止的明文数据库兼容混淆。
        var legacy=Path.Combine(root,"legacy-encrypted.crsbak");
        // 旧容器本身限额 128 MiB；使用独立小加密库构造历史格式，不整库读取大样本。
        using(var small=new VaultManager())
        {
            var smallDirectory=Path.Combine(root,"small-legacy-source"); await small.CreateAsync(smallDirectory,password); await TestStores.Enroll(small); small.Lock();
            var envelope=VaultFiles.ReadEnvelope(smallDirectory); using var smallKey=VaultKeyManager.Unlock(envelope,password);
            var database=File.ReadAllBytes(Path.Combine(smallDirectory,"crs.db"));
            var body=JsonSerializer.SerializeToUtf8Bytes(new VaultBackupBody(envelope,database,SHA256.HashData(database)));
            try {VaultFiles.AtomicWrite(legacy,JsonSerializer.SerializeToUtf8Bytes(new VaultBackupFile("CRS.Backup.v1",envelope,AuthenticatedEncryption.Seal(body,smallKey,Guid.NewGuid()))));}
            finally {CryptographicOperations.ZeroMemory(database); CryptographicOperations.ZeroMemory(body);}
        }
        await restored.RestoreAsync(legacy,Path.Combine(root,"legacy-restored"),password,false,password);
        // 截断和错误凭证不能提交任何恢复目标。
        var truncated=Path.Combine(root,"truncated.crsbak");
        using(var input=File.OpenRead(backup)) using(var output=File.Create(truncated)) {input.CopyTo(output); output.SetLength(output.Length-1);}
        var failed=Path.Combine(root,"no-partial-target");
        await RejectAsync(()=>restored.RestoreAsync(truncated,failed,password,false,password));
        if(Directory.Exists(failed) || Directory.GetDirectories(root,".crs-new-*").Length!=0) throw new Exception("拒绝恢复留下半成品目录。");
        Console.WriteLine("资源保护通过：读取前字节拒绝、集合／文本／深度限制、超限保存不留任务、分块换序／重复／截断／篡改／尾部拒绝、超过 128 MiB 库恢复、旧加密备份恢复。");
    }
    private static void ChunkAttacks(string root,VaultEnvelope envelope,SecretKey key)
    {
        // 两个完整块和一个尾块，确保分块边界及不足一整块的数据库尾部都被认证。
        var data=RandomNumberGenerator.GetBytes(ChunkedVaultBackup.ChunkBytes*2+4096);
        using var source=new MemoryStream(data); using var container=new MemoryStream();
        ChunkedVaultBackup.Write(source,container,envelope,key); var original=container.ToArray();
        void Extract(byte[] bytes)
        {
            using var input=new MemoryStream(bytes); var header=ChunkedVaultBackup.ReadHeader(input);
            var manifest=ChunkedVaultBackup.Authenticate(header,key);
            using var result=new MemoryStream(); ChunkedVaultBackup.Extract(input,result,manifest,key);
            if(!result.ToArray().SequenceEqual(data)) throw new Exception("分块往返内容不一致。");
        }
        Extract(original);
        var offset=ChunkedVaultBackup.Magic.Length;
        var headerLength=BitConverter.ToInt32(original,offset); offset+=4+headerLength;
        var firstLength=4+BitConverter.ToInt32(original,offset); var secondOffset=offset+firstLength;
        var secondLength=4+BitConverter.ToInt32(original,secondOffset);
        var swapped=original[..offset].Concat(original[secondOffset..(secondOffset+secondLength)])
            .Concat(original[offset..secondOffset]).Concat(original[(secondOffset+secondLength)..]).ToArray();
        Reject(()=>Extract(swapped));
        var repeated=original.ToArray(); original.AsSpan(offset,firstLength).CopyTo(repeated.AsSpan(secondOffset,firstLength));
        Reject(()=>Extract(repeated));
        Reject(()=>Extract(original[..^1])); Reject(()=>Extract(original.Concat(new byte[]{0}).ToArray()));
        var corrupted=original.ToArray(); corrupted[offset+firstLength-8]^=1; Reject(()=>Extract(corrupted));
        var badPrefix=original.ToArray(); BitConverter.GetBytes(int.MaxValue).CopyTo(badPrefix,offset); Reject(()=>Extract(badPrefix));
        var foreign=new MemoryStream(); source.Position=0; ChunkedVaultBackup.Write(source,foreign,envelope,key);
        var other=foreign.ToArray(); var otherOffset=ChunkedVaultBackup.Magic.Length+4+BitConverter.ToInt32(other,ChunkedVaultBackup.Magic.Length);
        var mixed=original.ToArray(); other.AsSpan(otherOffset,firstLength).CopyTo(mixed.AsSpan(offset,firstLength)); Reject(()=>Extract(mixed));
    }
    private static void Reject(Action action)
    {
        try {action();} catch(Exception e) when(e is CrsException or CryptographicException or JsonException or IOException) {return;}
        throw new Exception("资源或容器异常未被拒绝。");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try {await action();} catch(Exception e) when(e is CrsException or CryptographicException or JsonException or IOException) {return;}
        throw new Exception("损坏恢复未被拒绝。");
    }
}
