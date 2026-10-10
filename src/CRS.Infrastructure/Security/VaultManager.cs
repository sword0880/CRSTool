using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Security.Cryptography;
using CRS.Security.Vault;
using CRS.Security.Recovery;
using Dapper;

namespace CRS.Infrastructure;

/// <summary>单保险库适配器；本机槽由 Windows 用户保护，主密码及独立恢复槽保留跨机恢复能力。</summary>
public sealed partial class VaultManager : IVaultService
{
    private readonly object gate=new();
    private VaultSession? session;
    private VaultEnvelope? envelope;
    private LocalStore? repository;
    public bool IsUnlocked {get {lock(gate) return session is not null;}}
    public ICalculationRepository Repository {get {lock(gate) return repository??throw new CrsException("保险库未解锁。");}}
    public VaultState Inspect(string directory)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(directory)) return VaultState.Invalid;
            var db=Path.Combine(directory,"crs.db");
            if (!File.Exists(db)) return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()?VaultState.Invalid:VaultState.Missing;
            VaultFiles.RejectPlaintext(db); VaultFiles.ReadEnvelope(directory); return VaultState.Encrypted;
        }
        catch(CrsException error) { return error.Message.Contains("未加密")?VaultState.PlaintextRejected:VaultState.Invalid; }
        catch { return VaultState.Invalid; }
    }
    public Task<VaultCreationInfo> CreateAsync(string directory,string password)=>Task.Run(()=>
    {
        lock(gate)
        {
            if(session is not null || pending is not null) throw new CrsException("请先锁定当前保险库。");
            var staging=VaultFiles.BeginNewDirectory(directory);
            try
            {
                var created=VaultKeyManager.Create(password);
                using(var temporary=new VaultSession(staging,created.Key))
                {
                    _=new LocalStore(temporary,true);
                    VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(staging),JsonSerializer.SerializeToUtf8Bytes(created.Envelope));
                    temporary.Verify();
                    using var check=VaultKeyManager.Unlock(created.Envelope,password);
                }
                VaultAuthenticationInitialize(staging,created.Envelope,password);
                VaultFiles.CommitDirectory(staging,directory); UnlockCore(directory,password);
                return new VaultCreationInfo(created.RecoveryKey);
            }
            catch(CryptographicException ex) { throw new CrsException(ex.Message); }
            finally { Cleanup(staging); }
        }
    });
    public Task UnlockAsync(string directory,string password)=>Task.Run(()=> {lock(gate) { if(session is not null) throw new CrsException("请先锁定当前保险库。"); UnlockCore(directory,password); }});
    private void UnlockCore(string directory,string password)
    {
        ClearPending();
        if(System.Diagnostics.Stopwatch.GetTimestamp()<nextPasswordAttempt) throw new CrsException("主密码尝试过于频繁，请稍候重试。");
        VaultFiles.RejectPlaintext(Path.Combine(directory,"crs.db")); var metadata=VaultFiles.ReadEnvelope(directory);
        VaultSession? candidate=null;
        try
        {
            candidate=new(directory,VaultKeyManager.Unlock(metadata,password)); candidate.Verify();
            VaultAuthenticationStore.Ensure(candidate,metadata);
            envelope=metadata; pending=candidate; pendingStarted=System.Diagnostics.Stopwatch.GetTimestamp(); candidate=null;
            passwordFailures=0; nextPasswordAttempt=0;
            using(var c=pending.Open())
            {
                var state=VaultAuthenticationStore.Read(c,null,metadata);
                try {if(state.PhoneVerificationEnabled==0 && !VaultAuthenticationStore.NeedsEnrollment(state)) Promote();}
                finally {VaultAuthenticationStore.Clear(state);}
            }
        }
        catch(CryptographicException) {passwordFailures++; nextPasswordAttempt=System.Diagnostics.Stopwatch.GetTimestamp()+(long)(System.Diagnostics.Stopwatch.Frequency*Math.Min(8,passwordFailures)); throw new CrsException("主密码错误或保险库认证失败。"); }
        finally { candidate?.Dispose(); }
    }
    public Task ChangePasswordAsync(string oldPassword,string newPassword,string code)=>Task.Run(()=>
    {
        lock(gate)
        {
            var active=Required();
            using var proof=VaultKeyManager.Unlock(envelope!,oldPassword);
            VaultAuthenticationStore.Consume(active,envelope!,code,Now());
            var changed=VaultKeyManager.ChangePassword(envelope!,proof,newPassword);
            VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(active.DirectoryPath),JsonSerializer.SerializeToUtf8Bytes(changed),true); envelope=changed;
        }
    });
    public Task ResetPasswordAsync(string directory,string recoveryKey,string newPassword)=>Task.Run(()=>
    {
        lock(gate)
        {
            if(session is not null) throw new CrsException("请先锁定当前保险库。");
            VaultFiles.RejectPlaintext(Path.Combine(directory,"crs.db")); var metadata=VaultFiles.ReadEnvelope(directory);
            using var recovered=RecoveryKeyManager.Recover(metadata,recoveryKey);
            ClearPending();
            using var check=new VaultSession(directory,recovered.Clone()); check.Verify(); VaultAuthenticationStore.Ensure(check,metadata);
            var changed=VaultKeyManager.ChangePassword(metadata,recovered,newPassword);
            ForceRecovery(check,changed);
            VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(directory),JsonSerializer.SerializeToUtf8Bytes(changed),true);
            envelope=changed; pending=new VaultSession(directory,recovered.Clone()); pendingStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        }
    });
    /// <summary>SQLite 在线备份 API 生成独立加密快照，经验证后封装；不直接复制正在写入的数据库。</summary>
    public Task BackupAsync(string path)=>Task.Run(()=>
    {
        lock(gate)
        {
            var active=Required();
            active.Use(()=>
            {
                active.Verify();
                var snapshot=Path.Combine(active.DirectoryPath,".backup-"+Guid.NewGuid().ToString("N")+".db");
                byte[]? bytes=null; byte[]? body=null;
                try
                {
                    using(var source=active.Open()) using(var destination=active.Open(snapshot,true)) source.BackupDatabase(destination);
                    using(var c=active.Open(snapshot)) { if(c.ExecuteScalar<string>("PRAGMA integrity_check")!="ok" || c.Query<string>("PRAGMA cipher_integrity_check").Any(s=>s!="ok")) throw new CrsException("备份快照完整性校验失败。"); }
                    bytes=File.ReadAllBytes(snapshot);
                    if(bytes.Length>128*1024*1024) throw new CrsException("当前备份支持最大 128 MB 加密数据库。");
                    body=JsonSerializer.SerializeToUtf8Bytes(new BackupBody(envelope!,bytes,SHA256.HashData(bytes)));
                    using var key=active.CloneKey(); var sealedBody=AuthenticatedEncryption.Seal(body,key,Guid.NewGuid());
                    var backup=new BackupFile("CRS.Backup.v1",envelope!,sealedBody);
                    // 先验证容器确实可解封再原子提交，不覆盖已有备份。
                    var verify=AuthenticatedEncryption.Open(sealedBody,key); CryptographicOperations.ZeroMemory(verify);
                    VaultFiles.AtomicWrite(Path.GetFullPath(path),JsonSerializer.SerializeToUtf8Bytes(backup));
                }
                finally { Clear(bytes); Clear(body); foreach(var suffix in new[]{"","-wal","-shm","-journal"}) if(File.Exists(snapshot+suffix)) File.Delete(snapshot+suffix); }
            });
        }
    });
    /// <summary>恢复先认证容器、核对摘要和加密库完整性，成功后才将暂存目录提交到新空位置。</summary>
    public Task RestoreAsync(string backupPath,string targetDirectory,string secret,bool useRecovery,string newPassword)=>Task.Run(()=>
    {
        lock(gate)
        {
            if(session is not null || pending is not null) throw new CrsException("请先锁定当前保险库。");
            using var stream=File.OpenRead(backupPath);
            if(stream.Length is <=0 or >256*1024*1024) throw new CrsException("备份大小无效，最大支持 256 MB 容器。");
            var backup=JsonSerializer.Deserialize<BackupFile>(stream,VaultFiles.Options)??throw new CrsException("备份格式无效。");
            if(backup.Schema!="CRS.Backup.v1") throw new CrsException("备份版本不支持。");
            using var key=useRecovery?RecoveryKeyManager.Recover(backup.Envelope,secret):VaultKeyManager.Unlock(backup.Envelope,secret);
            var plain=AuthenticatedEncryption.Open(backup.Payload,key); string? staging=null;
            try
            {
                var body=JsonSerializer.Deserialize<BackupBody>(plain,VaultFiles.Options)??throw new CrsException("备份正文无效。");
                try
                {
                    if(JsonSerializer.Serialize(body.Envelope)!=JsonSerializer.Serialize(backup.Envelope)
                        || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(body.Database),body.Sha256)) throw new CrsException("备份元数据或数据库摘要不一致。");
                    staging=VaultFiles.BeginNewDirectory(targetDirectory);
                    VaultFiles.AtomicWrite(Path.Combine(staging,"crs.db"),body.Database);
                    var changed=VaultKeyManager.ChangePassword(body.Envelope,key,newPassword);
                    VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(staging),JsonSerializer.SerializeToUtf8Bytes(changed));
                    using(var check=new VaultSession(staging,key.Clone())) { check.Verify(); VaultAuthenticationStore.Ensure(check,changed); _=new LocalStore(check); }
                    VaultFiles.CommitDirectory(staging,targetDirectory);
                }
                finally { Clear(body.Database); }
            }
            finally { Clear(plain); if(staging is not null) Cleanup(staging); }
        }
    });
    public void Lock() {lock(gate) {ClearPending(); session?.Dispose(); session=null; repository=null; envelope=null;}}
    public void Dispose()=>Lock();
    internal void Execute(Action action) {lock(gate) Required().Use(action);}
    /// <summary>捕获当前不可复活会话，防止旧导出任务借后续新解锁继续写入。</summary>
    internal Action<Action> CaptureGuard() {lock(gate) return Required().Use;}
    internal Microsoft.Data.Sqlite.SqliteConnection OpenForTest()=>Required().Open();
    private VaultSession Required()=>session??throw new CrsException("保险库未解锁。");
    // 仅清理本组件随机创建的同级暂存目录，绝不删除目标或旧数据库目录。
    private static void Cleanup(string staging) { if(Directory.Exists(staging) && Path.GetFileName(staging).StartsWith(".crs-new-",StringComparison.Ordinal)) Directory.Delete(staging,true); }
    private static void Clear(byte[]? data) {if(data is not null) CryptographicOperations.ZeroMemory(data);}
    private sealed record BackupFile(string Schema,VaultEnvelope Envelope,SealedPayload Payload);
    private sealed record BackupBody(VaultEnvelope Envelope,byte[] Database,byte[] Sha256);
}
