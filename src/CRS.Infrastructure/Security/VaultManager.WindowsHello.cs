using CRS.Application.Abstractions;
using CRS.Security.Vault;
using System.Security.Cryptography;
using Dapper;

namespace CRS.Infrastructure;

public sealed partial class VaultManager
{
    public Task<bool> IsWindowsHelloAvailableAsync()=>hello?.IsAvailableAsync()??Task.FromResult(false);

    /// <summary>仅已认证会话可切换；手机到 Hello 验证两端，Hello 到手机先验证 Hello，再进入绑定。</summary>
    public async Task SetLoginMethodAsync(bool useWindowsHello,string password,string code)
    {
        var generation=CaptureOperation(); VaultSession expected; bool wasHello;var identity=hello?.UserIdentity;
        lock(gate)
        {
            expected=Required(); wasHello=UsesWindowsHello;
            if(string.IsNullOrEmpty(password)) throw new CrsException("请输入主密码，以确认修改验证方式。");
            if(!wasHello && (code is null || code.Length!=6 || !code.All(c=>c is >= '0' and <= '9')))
                throw new CrsException("请输入当前手机上的新六位验证码，以确认修改验证方式。");
            try {using var proof=VaultKeyManager.Unlock(envelope!,password);}
            catch(CryptographicException) {throw new CrsException("主密码错误或保险库认证失败，验证方式未修改。请填写创建数据库时设置的主密码。");}
            if(!wasHello) VaultAuthenticationStore.Consume(expected,envelope!,code,Now());
        }
        if(useWindowsHello || wasHello)
        {
            var verifier=hello??throw new CrsException("此设备未提供 Windows Hello 验证。");
            if(!await verifier.IsAvailableAsync()) throw new CrsException("请先在 Windows 设置中配置 Windows Hello；当前只能使用手机验证。");
            await verifier.VerifyAsync(generation).ConfigureAwait(false);
        }
        lock(gate)
        {
            generation.ThrowIfCancellationRequested();
            if(!ReferenceEquals(session,expected)) throw new CrsException("原安全会话已失效，请重新打开设置。");
            using var c=expected.Open(); using var tx=c.BeginTransaction();var s=VaultAuthenticationStore.Read(c,tx,envelope!);
            try
            {
                if(s.SecurityVersion==1) c.Execute("ALTER TABLE SecuritySettings ADD COLUMN PhoneVerificationEnabled INTEGER NOT NULL DEFAULT 1",transaction:tx);
                if(s.SecurityVersion<3) c.Execute("ALTER TABLE SecuritySettings ADD COLUMN LoginMethod TEXT NOT NULL DEFAULT 'Totp'; ALTER TABLE SecuritySettings ADD COLUMN HelloIdentity TEXT",transaction:tx);
                if(useWindowsHello)
                {
                    if(hello!.UserIdentity!=identity) throw new CrsException("Windows 身份已改变，请重新开始。");
                    if(string.IsNullOrWhiteSpace(identity) || identity.Length>256) throw new CrsException("无法识别当前 Windows 用户。");
                    c.Execute("UPDATE SecuritySettings SET SecurityVersion=3,LoginMethod='WindowsHello',HelloIdentity=@identity,MfaStatus='HelloEnabled',InitializationComplete=1,PhoneVerificationEnabled=1,TotpSecret=NULL,PendingTotpSecret=NULL,LastAcceptedStep=NULL,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1",new{identity},tx);
                }
                else if(wasHello)
                    c.Execute("UPDATE SecuritySettings SET SecurityVersion=3,LoginMethod='Totp',HelloIdentity=NULL,MfaStatus='Pending',InitializationComplete=0,PhoneVerificationEnabled=1,TotpSecret=NULL,PendingTotpSecret=NULL,LastAcceptedStep=NULL,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1",transaction:tx);
                else c.Execute("UPDATE SecuritySettings SET SecurityVersion=3,PhoneVerificationEnabled=1 WHERE Id=1",transaction:tx);
                if(useWindowsHello || wasHello) c.Execute("DELETE FROM RecoveryCodes",transaction:tx);
                VaultAuthenticationStore.Event(c,tx,envelope!,"LoginMethodChanged",useWindowsHello?"WindowsHello":"Totp",Now());tx.Commit();
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
        if(wasHello && !useWindowsHello) Lock();
    }
    public bool UsesWindowsHello
    {
        get { lock(gate) { var current=session??pending; if(current is null) return false;
            using var c=current.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
            try {return s.LoginMethod=="WindowsHello";} finally {VaultAuthenticationStore.Clear(s);} } }
    }
    public bool CanChooseWindowsHello
    {
        get { lock(gate) {if(pending is null || awaitingConfirmation || replacingPhone || hello is null) return false;
            using var c=pending.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
            try {return s.LoginMethod=="Totp" && s.MfaStatus=="Pending" && s.InitializationComplete==0;}
            finally {VaultAuthenticationStore.Clear(s);} } }
    }
    public Task EnrollWindowsHelloAsync(bool recoverySaved)=>VerifyHelloCoreAsync(true,recoverySaved);
    public Task VerifyWindowsHelloAsync()=>VerifyHelloCoreAsync(false,true);

    private async Task VerifyHelloCoreAsync(bool enroll,bool recoverySaved)
    {
        var verifier=hello??throw new CrsException("此设备未提供 Windows Hello 验证。");
        var generation=CaptureOperation(); VaultSession expected; string identity;
        lock(gate)
        {
            generation.ThrowIfCancellationRequested(); expected=Pending(); identity=verifier.UserIdentity;
            if(string.IsNullOrWhiteSpace(identity) || identity.Length>256) throw new CrsException("无法识别当前 Windows 用户。");
            if(enroll && (!CanChooseWindowsHello || !recoverySaved)) throw new CrsException("仅首次未绑定时可选择 Hello，请先离线保存数据库恢复密钥。");
            using var c=expected.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
            try
            {
                if(!enroll && (s.LoginMethod!="WindowsHello" || awaitingConfirmation)) throw new CrsException("当前必须使用已绑定的 TOTP，不能切换到 Hello。");
                if(!enroll && (s.HelloIdentity!=identity || VaultAuthenticationStore.NeedsEnrollment(s)) && !pendingPasswordProof)
                    throw new CrsException("Windows 身份已改变或需要恢复，请先使用主密码或数据库恢复密钥，再明确验证 Hello。");
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
        // 操作系统弹窗期间不持有数据库门闩；锁屏必须立即使旧请求失效。
        await verifier.VerifyAsync(generation).ConfigureAwait(false);
        lock(gate)
        {
            generation.ThrowIfCancellationRequested();
            if(!ReferenceEquals(pending,expected) || verifier.UserIdentity!=identity || !ReferenceEquals(Pending(),expected))
                throw new CrsException("认证会话已改变，请重新开始。");
            using var c=expected.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!);
            try
            {
                if(enroll)
                {
                    if(s.LoginMethod!="Totp" || s.MfaStatus!="Pending" || s.InitializationComplete!=0 || awaitingConfirmation || replacingPhone)
                        throw new CrsException("首次选择状态已改变。");
                    // 只在操作系统成功验证后升级结构；取消不会留下半完成绑定。
                    if(s.SecurityVersion==1) c.Execute("ALTER TABLE SecuritySettings ADD COLUMN PhoneVerificationEnabled INTEGER NOT NULL DEFAULT 1",transaction:tx);
                    if(s.SecurityVersion<3) c.Execute("ALTER TABLE SecuritySettings ADD COLUMN LoginMethod TEXT NOT NULL DEFAULT 'Totp'; ALTER TABLE SecuritySettings ADD COLUMN HelloIdentity TEXT",transaction:tx);
                }
                else if(s.LoginMethod!="WindowsHello" || ((s.HelloIdentity!=identity || VaultAuthenticationStore.NeedsEnrollment(s)) && !pendingPasswordProof))
                    throw new CrsException("认证方式或恢复状态已改变，请重新使用恢复凭证。");
                c.Execute("UPDATE SecuritySettings SET SecurityVersion=3,LoginMethod='WindowsHello',HelloIdentity=@identity,MfaStatus='HelloEnabled',InitializationComplete=1,PhoneVerificationEnabled=1,TotpSecret=NULL,PendingTotpSecret=NULL,LastAcceptedStep=NULL,FailedAttempts=0,LockedUntilUtc=NULL WHERE Id=1",new {identity},tx);
                c.Execute("DELETE FROM RecoveryCodes",transaction:tx);
                VaultAuthenticationStore.Event(c,tx,envelope!,enroll?"HelloEnrolled":"HelloVerified","Success",Now()); tx.Commit(); Promote();
            }
            finally {VaultAuthenticationStore.Clear(s);}
        }
    }
}
