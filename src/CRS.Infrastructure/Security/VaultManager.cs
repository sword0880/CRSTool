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
    private readonly IWindowsHelloVerifier? hello;
    public VaultManager(IWindowsHelloVerifier? hello = null) { this.hello=hello; }
    // 主密码或恢复密钥证明，只用于显式重新登记 Hello 的 Windows 身份。
    private bool pendingPasswordProof;
    private readonly object gate=new();
    private readonly object operationGate=new();
    private CancellationTokenSource operationLifetime=new();
    private CancellationToken CaptureOperation() {lock(operationGate) return operationLifetime.Token;}
    private VaultSession? session;
    private VaultEnvelope? envelope;
    private LocalStore? repository;
    public bool IsUnlocked {get {lock(gate) return session is not null;}}
    public VaultBackupCapacity? BackupCapacity
    {
        get
        {
            lock(gate)
            {
                if (session is null) return null;
                return session.Use(() =>
                {
                    using var connection=session.Open();
                    using var transaction=connection.BeginTransaction(deferred:true);
                    var bytes=connection.ExecuteScalar<long>("PRAGMA page_count",transaction:transaction)
                        * connection.ExecuteScalar<long>("PRAGMA page_size",transaction:transaction);
                    return new VaultBackupCapacity(bytes,ChunkedVaultBackup.MaxDatabaseBytes);
                });
            }
        }
    }
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
            envelope=metadata; pending=candidate; pendingPasswordProof=true; pendingStarted=System.Diagnostics.Stopwatch.GetTimestamp(); candidate=null;
            passwordFailures=0; nextPasswordAttempt=0;
            using(var c=pending.Open())
            {
                var state=VaultAuthenticationStore.Read(c,null,metadata);
                VaultAuthenticationStore.Clear(state);
            }
        }
        catch(CryptographicException) {passwordFailures++; nextPasswordAttempt=System.Diagnostics.Stopwatch.GetTimestamp()+(long)(System.Diagnostics.Stopwatch.Frequency*Math.Min(8,passwordFailures)); throw new CrsException("主密码错误或保险库认证失败。"); }
        finally { candidate?.Dispose(); }
    }
    public async Task ChangePasswordAsync(string oldPassword,string newPassword,string code)
    {
        var generation=CaptureOperation(); VaultSession expected; bool useHello;
        lock(gate) {generation.ThrowIfCancellationRequested(); expected=Required(); useHello=UsesWindowsHello;}
        // Hello 方式改密同样重新验证；系统弹窗不占用锁定所需的门闩。
        if(useHello) await (hello??throw new CrsException("Windows Hello 不可用。")).VerifyAsync(generation).ConfigureAwait(false);
        await Task.Run(()=>
        {
        lock(gate)
        {
            generation.ThrowIfCancellationRequested();
            var active=Required();
            if(!ReferenceEquals(active,expected)) throw new CrsException("改密会话已失效。");
            using var proof=VaultKeyManager.Unlock(envelope!,oldPassword);
            if(!useHello) VaultAuthenticationStore.Consume(active,envelope!,code,Now());
            var changed=VaultKeyManager.ChangePassword(envelope!,proof,newPassword);
            VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(active.DirectoryPath),JsonSerializer.SerializeToUtf8Bytes(changed),true); envelope=changed;
        }
        }).ConfigureAwait(false);
    }
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
            envelope=changed; pending=new VaultSession(directory,recovered.Clone()); pendingPasswordProof=true; pendingStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        }
    });
    /// <summary>SQLite 在线备份 API 生成独立加密快照，经验证后封装；不直接复制正在写入的数据库。</summary>
    public Task BackupAsync(string path,CancellationToken cancellation=default)=>BackupCoreAsync(path,cancellation);
    // 单次调用的进度探针只供隔离故障验证，不引入全局开关或跳过认证。
    internal Task BackupCoreAsync(string path,CancellationToken cancellation=default,Action<CancellationToken>? afterStep=null)
    {
        var generation=CaptureOperation();
        return Task.Run(()=>
        {
        lock(gate)
        {
            generation.ThrowIfCancellationRequested();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(generation,cancellation);
            var active=Required();
            try {VaultBackupFiles.Write(active,envelope!,path,linked.Token,afterStep);}
            catch(Exception) when(linked.IsCancellationRequested) {throw new OperationCanceledException(linked.Token);}
        }
        });
    }
    /// <summary>恢复先认证容器、核对摘要和加密库完整性，成功后才将暂存目录提交到新空位置。</summary>
    public Task RestoreAsync(string backupPath,string targetDirectory,string secret,bool useRecovery,string newPassword,CancellationToken cancellation=default)
    {
        var generation=CaptureOperation();
        return Task.Run(()=>
        {
        lock(gate)
        {
            generation.ThrowIfCancellationRequested();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(generation,cancellation); cancellation=linked.Token;
            cancellation.ThrowIfCancellationRequested();
            if(session is not null || pending is not null) throw new CrsException("请先锁定当前保险库。");
            using var stream=File.OpenRead(backupPath);
            // 固定头区分新分块容器与旧加密 JSON；不提供明文库兼容或转换。
            Span<byte> prefix = stackalloc byte[ChunkedVaultBackup.Magic.Length];
            prefix.Clear();
            if (stream.Length >= prefix.Length) stream.ReadExactly(prefix);
            stream.Position = 0;
            if (prefix.SequenceEqual(ChunkedVaultBackup.Magic))
            {
                RestoreChunked(stream,targetDirectory,secret,useRecovery,newPassword,cancellation); return;
            }
            if(stream.Length is <=0 or >256*1024*1024) throw new CrsException("备份大小无效，最大支持 256 MB 容器。");
            var backup=JsonSerializer.Deserialize<VaultBackupFile>(stream,VaultFiles.Options)??throw new CrsException("备份格式无效。");
            if(backup.Schema!="CRS.Backup.v1") throw new CrsException("备份版本不支持。");
            using var key=useRecovery?RecoveryKeyManager.Recover(backup.Envelope,secret):VaultKeyManager.Unlock(backup.Envelope,secret);
            cancellation.ThrowIfCancellationRequested();
            var plain=AuthenticatedEncryption.Open(backup.Payload,key); string? staging=null;
            try
            {
                var body=JsonSerializer.Deserialize<VaultBackupBody>(plain,VaultFiles.Options)??throw new CrsException("备份正文无效。");
                try
                {
                    if(body.Database is null || body.Database.Length is <=0 or >128*1024*1024 || body.Sha256 is not {Length:32}
                        || JsonSerializer.Serialize(body.Envelope)!=JsonSerializer.Serialize(backup.Envelope)
                        || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(body.Database),body.Sha256)) throw new CrsException("备份元数据或数据库摘要不一致。");
                    staging=VaultFiles.BeginNewDirectory(targetDirectory);
                    VaultFiles.AtomicWrite(Path.Combine(staging,"crs.db"),s=>s.Write(body.Database),cancellation:cancellation);
                    var changed=VaultKeyManager.ChangePassword(body.Envelope,key,newPassword);
                    VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(staging),JsonSerializer.SerializeToUtf8Bytes(changed));
                    using(var check=new VaultSession(staging,key.Clone()))
                    { using var registration=cancellation.Register(check.RequestLock); check.Verify(); VaultAuthenticationStore.Ensure(check,changed); _=new LocalStore(check); }
                    cancellation.ThrowIfCancellationRequested();
                    VaultFiles.CommitDirectory(staging,targetDirectory);
                }
                finally { Clear(body.Database); }
            }
            finally { Clear(plain); if(staging is not null) Cleanup(staging); }
        }
        });
    }
    /// <summary>分块逐一认证后只写加密数据库字节；全部块和数据库校验通过才提交新目录。</summary>
    private static void RestoreChunked(Stream stream,string target,string secret,bool recovery,string password,CancellationToken cancellation)
    {
        if (stream.Length is <=0 or >3L*1024*1024*1024) throw new CrsException("分块备份容器超过 3 GiB 上限。");
        var header = ChunkedVaultBackup.ReadHeader(stream);
        using var key = recovery ? RecoveryKeyManager.Recover(header.Envelope,secret) : VaultKeyManager.Unlock(header.Envelope,secret);
        var manifest = ChunkedVaultBackup.Authenticate(header,key);
        cancellation.ThrowIfCancellationRequested();
        string? staging = null;
        try
        {
            staging = VaultFiles.BeginNewDirectory(target);
            using (var database = new FileStream(Path.Combine(staging,"crs.db"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                ChunkedVaultBackup.Extract(stream,database,manifest,key,cancellation); database.Flush(true);
            }
            var changed = VaultKeyManager.ChangePassword(manifest.Envelope,key,password);
            VaultFiles.AtomicWrite(VaultFiles.EnvelopePath(staging),JsonSerializer.SerializeToUtf8Bytes(changed));
            using (var check = new VaultSession(staging,key.Clone()))
            { using var registration=cancellation.Register(check.RequestLock); check.Verify(); VaultAuthenticationStore.Ensure(check,changed); _=new LocalStore(check); }
            cancellation.ThrowIfCancellationRequested();
            VaultFiles.CommitDirectory(staging,target);
        }
        finally { if (staging is not null) Cleanup(staging); }
    }
    public void Lock()
    {
        // 先发出不可复活的取消，再等待事务退出；不能先等门闩再通知后台停止。
        lock(operationGate) operationLifetime.Cancel();
        Volatile.Read(ref session)?.RequestLock(); Volatile.Read(ref pending)?.RequestLock();
        lock(gate)
        {
            ClearPending(); session?.Dispose(); session=null; repository=null; envelope=null;
            lock(operationGate) {operationLifetime.Cancel(); operationLifetime.Dispose(); operationLifetime=new();}
        }
    }
    public void Dispose()=>Lock();
    internal void Execute(Action action) {lock(gate) Required().Use(action);}
    /// <summary>捕获当前不可复活会话，防止旧导出任务借后续新解锁继续写入。</summary>
    internal Action<Action> CaptureGuard() {lock(gate) return Required().Use;}
    internal Microsoft.Data.Sqlite.SqliteConnection OpenForTest()=>Required().Open();
    private VaultSession Required()=>session??throw new CrsException("保险库未解锁。");
    // 仅清理本组件随机创建的同级暂存目录，绝不删除目标或旧数据库目录。
    private static void Cleanup(string staging) { if(Directory.Exists(staging) && Path.GetFileName(staging).StartsWith(".crs-new-",StringComparison.Ordinal)) Directory.Delete(staging,true); }
    private static void Clear(byte[]? data) {if(data is not null) CryptographicOperations.ZeroMemory(data);}
}
