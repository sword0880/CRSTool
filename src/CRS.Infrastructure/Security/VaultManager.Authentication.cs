using System.Diagnostics;
using System.Security.Cryptography;
using CRS.Application.Abstractions;
using CRS.Security.Authentication;
using CRS.Security.Vault;
using Dapper;
using QRCoder;

namespace CRS.Infrastructure;

/// <summary>主密码只建立五分钟受限上下文；手机验证成功后才发布业务仓储。</summary>
public sealed partial class VaultManager
{
    private VaultSession? pending;
    private long pendingStarted;
    private bool awaitingConfirmation;
    private bool replacingPhone;
    private long nextAttempt;
    private long cooldownUntil;
    private int passwordFailures;
    private long nextPasswordAttempt;
    internal Func<long> Clock {get;set;}=()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long Now()=>Clock();
    public AuthenticationState Authentication
    {
        get {lock(gate)
        {
            if(session is not null) return AuthenticationState.Authenticated;
            if(pending is null) return AuthenticationState.Locked;
            if(Stopwatch.GetElapsedTime(pendingStarted)>TimeSpan.FromMinutes(5)) {ClearPending(); return AuthenticationState.Locked;}
            if(awaitingConfirmation) return AuthenticationState.AwaitingRecoveryConfirmation;
            if(replacingPhone) return AuthenticationState.PendingEnrollment;
            using var c=pending.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
            try {return VaultAuthenticationStore.NeedsEnrollment(s)?AuthenticationState.PendingEnrollment:AuthenticationState.PendingMfa;}
            finally {VaultAuthenticationStore.Clear(s);}
        }}
    }
    private VaultSession Pending()
    {
        if(pending is null || Stopwatch.GetElapsedTime(pendingStarted)>TimeSpan.FromMinutes(5)) {ClearPending(); throw new CrsException("认证已过期，请重新输入主密码。");}
        if(Stopwatch.GetTimestamp()<nextAttempt) throw new CrsException("请稍候再试验证码。");
        if(Stopwatch.GetTimestamp()<cooldownUntil) throw new CrsException("验证码仍在 15 分钟冷却中。");
        return pending;
    }
    private void ClearPending() {pending?.Dispose(); pending=null; awaitingConfirmation=false; replacingPhone=false; nextAttempt=0;}
    private static void VaultAuthenticationInitialize(string directory,VaultEnvelope metadata,string password)
    {
        using var check=new VaultSession(directory,VaultKeyManager.Unlock(metadata,password)); VaultAuthenticationStore.Ensure(check,metadata);
    }
    public Task<EnrollmentImage> BeginEnrollmentAsync()=>Task.Run(()=>
    {
        lock(gate)
        {
            var candidate=Pending(); using var c=candidate.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!);
            byte[]? secret=null;
            try
            {
                if((!VaultAuthenticationStore.NeedsEnrollment(s) && !replacingPhone) || awaitingConfirmation) throw new CrsException("当前不允许绑定，请先验证旧手机或使用恢复方式。");
                VaultAuthenticationStore.CheckCooldown(s,Now()); secret=s.PendingTotpSecret??Totp.GenerateSecret();
                c.Execute("UPDATE SecuritySettings SET PendingTotpSecret=@secret WHERE Id=1",new {secret},tx); tx.Commit();
                // URI 和二维码仅留在受限绑定操作与内存中，不写入图片文件或通用设置。
                using var qr=QRCodeGenerator.GenerateQrCode(Totp.EnrollmentUri(secret,envelope!.VaultId),QRCodeGenerator.ECCLevel.M);
                using var image=new PngByteQRCode(qr); return new EnrollmentImage(image.GetGraphic(6));
            }
            finally {if(secret is not null) CryptographicOperations.ZeroMemory(secret); VaultAuthenticationStore.Clear(s);}
        }
    });
    public Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(string code)=>Task.Run<IReadOnlyList<string>>(()=>
    {
        lock(gate)
        {
            var candidate=Pending(); using var c=candidate.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!); var now=Now();
            try
            {
                if((!VaultAuthenticationStore.NeedsEnrollment(s) && !replacingPhone) || awaitingConfirmation || s.PendingTotpSecret is null) throw new CrsException("请先扫描绑定二维码。");
                VaultAuthenticationStore.CheckCooldown(s,now);
                var step=Totp.Match(s.PendingTotpSecret,code,now,null);
                if(step is null) {RegisterDelay(s,true); VaultAuthenticationStore.Failure(c,tx,envelope!,s,now);}
                c.Execute("UPDATE SecuritySettings SET TotpSecret=PendingTotpSecret,PendingTotpSecret=NULL,MfaStatus='Enabled',LastAcceptedStep=@step,FailedAttempts=0,LockedUntilUtc=NULL,InitializationComplete=0 WHERE Id=1",new {step},tx);
                c.Execute("DELETE FROM RecoveryCodes",transaction:tx); var codes=new List<string>(8);
                for(var i=0;i<8;i++)
                {
                    var recovery=MfaRecoveryCodes.Generate(); var salt=RandomNumberGenerator.GetBytes(16); var hash=MfaRecoveryCodes.Hash(recovery,salt); codes.Add(recovery);
                    try {c.Execute("INSERT INTO RecoveryCodes VALUES(@id,@salt,@hash,@now,NULL)",new {id=Guid.NewGuid().ToString("N"),salt,hash,now},tx);}
                    finally {CryptographicOperations.ZeroMemory(salt); CryptographicOperations.ZeroMemory(hash);}
                }
                VaultAuthenticationStore.Event(c,tx,envelope!,"EnrollmentVerified","Success",now); tx.Commit(); awaitingConfirmation=true; return codes;
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
    });
    public Task CompleteEnrollmentAsync(bool recoverySaved)=>Task.Run(()=>
    {
        lock(gate)
        {
            var candidate=Pending(); if(!awaitingConfirmation || !recoverySaved) throw new CrsException("请先保存数据库恢复密钥和手机恢复码。");
            using var c=candidate.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!);
            try
            {
                if(s.MfaStatus!="Enabled" || s.InitializationComplete!=0 || c.ExecuteScalar<int>("SELECT COUNT(*) FROM RecoveryCodes WHERE UsedAtUtc IS NULL",transaction:tx)!=8) throw new CrsException("绑定确认状态无效。");
                c.Execute("UPDATE SecuritySettings SET InitializationComplete=1 WHERE Id=1",transaction:tx);
                VaultAuthenticationStore.Event(c,tx,envelope!,"EnrollmentCompleted","Success",Now()); tx.Commit(); Promote();
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
    });
    public Task VerifyTotpAsync(string code)=>Task.Run(()=>
    {
        lock(gate)
        {
            var candidate=Pending();
            try {VaultAuthenticationStore.Consume(candidate,envelope!,code,Now()); Promote();}
            catch(CrsException)
            {
                using var c=candidate.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
                try {RegisterDelay(s);} finally {VaultAuthenticationStore.Clear(s);} throw;
            }
        }
    });
    private void RegisterDelay(VaultAuthenticationStore.State s,bool beforeFailure=false)
    {
        nextAttempt=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*Math.Min(8,Math.Max(1,s.FailedAttempts)));
        var attempts=s.LockedUntilUtc is not null && s.LockedUntilUtc<=Now()?(beforeFailure?1:0):s.FailedAttempts+(beforeFailure?1:0);
        if(attempts>=5 || s.LockedUntilUtc>Now()) cooldownUntil=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*900d);
    }
    private void Promote()
    {
        // 发布全新的业务会话，旧会话永远不能复活。
        var candidate=Pending(); var store=new LocalStore(candidate); session=candidate; repository=store; pending=null; awaitingConfirmation=false; nextAttempt=0;
    }
    public Task RecoverMfaAsync(string recoveryCode)=>Task.Run(()=>
    {
        lock(gate)
        {
            var candidate=Pending(); using var c=candidate.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!); var now=Now();
            try
            {
                if(VaultAuthenticationStore.NeedsEnrollment(s)) throw new CrsException("请完成新手机绑定。");
                VaultAuthenticationStore.CheckCooldown(s,now); string? matched=null;
                foreach(var row in c.Query<(string Id,byte[] CodeSalt,byte[] CodeHash)>("SELECT Id,CodeSalt,CodeHash FROM RecoveryCodes WHERE UsedAtUtc IS NULL",transaction:tx))
                {
                    byte[]? hash=null;
                    try {hash=MfaRecoveryCodes.Hash(recoveryCode,row.CodeSalt); if(CryptographicOperations.FixedTimeEquals(hash,row.CodeHash)) matched=row.Id;}
                    catch(CryptographicException){}
                    finally {if(hash is not null) CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(row.CodeSalt); CryptographicOperations.ZeroMemory(row.CodeHash);}
                }
                if(matched is null) {RegisterDelay(s,true); VaultAuthenticationStore.Failure(c,tx,envelope!,s,now);}
                if(c.Execute("UPDATE RecoveryCodes SET UsedAtUtc=@now WHERE Id=@matched AND UsedAtUtc IS NULL",new {now,matched},tx)!=1) throw new CrsException("手机恢复码已使用。");
                c.Execute("UPDATE SecuritySettings SET MfaStatus='RecoveryOnly',InitializationComplete=0,PendingTotpSecret=NULL,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1",transaction:tx);
                VaultAuthenticationStore.Event(c,tx,envelope!,"MfaRecoveryConsumed","Success",now); tx.Commit();
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
    });
    private void ForceRecovery(VaultSession candidate,VaultEnvelope metadata)
    {
        using var c=candidate.Open(); using var tx=c.BeginTransaction();
        c.Execute("UPDATE SecuritySettings SET MfaStatus='RecoveryOnly',InitializationComplete=0,PendingTotpSecret=NULL,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1",transaction:tx);
        VaultAuthenticationStore.Event(c,tx,metadata,"PasswordRecovery","Success",Now()); tx.Commit();
    }
    public Task BeginPhoneReplacementAsync(string password,string code)=>Task.Run(()=>
    {
        lock(gate)
        {
            var active=Required(); using var proof=VaultKeyManager.Unlock(envelope!,password);
            VaultAuthenticationStore.Consume(active,envelope!,code,Now());
            // 仅持久标记待替换，不撤销旧绑定；失败或取消后仍能使用旧手机正常登录。
            var candidate=new VaultSession(active.DirectoryPath,proof.Clone());
            using(var c=candidate.Open()) c.Execute("UPDATE SecuritySettings SET PendingTotpSecret=NULL WHERE Id=1");
            active.Dispose(); session=null; repository=null; pending=candidate; pendingStarted=Stopwatch.GetTimestamp();
            replacingPhone=true;
        }
    });
    // 测试只读取已经通过密码建立的认证上下文，不改变真实生产认证规则。
    internal string CodeForTest(bool enrollment=false,long? step=null)
    {
        lock(gate)
        {
            using var c=(pending??session??throw new CrsException("没有测试会话。")).Open();
            var secret=c.ExecuteScalar<byte[]>(enrollment?"SELECT PendingTotpSecret FROM SecuritySettings WHERE Id=1":"SELECT TotpSecret FROM SecuritySettings WHERE Id=1")??throw new CrsException("测试绑定尚未建立。");
            try {return Totp.Compute(secret,step??Now()/30);} finally {CryptographicOperations.ZeroMemory(secret);}
        }
    }
}
