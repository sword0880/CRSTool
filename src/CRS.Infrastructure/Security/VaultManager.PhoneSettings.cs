using CRS.Security.Vault;
using Dapper;

namespace CRS.Infrastructure;

public sealed partial class VaultManager
{
    /// <summary>从加密认证表读取开关，不信任 settings.json 中的登录选项。</summary>
    public bool PhoneVerificationEnabled
    {
        get {lock(gate)
        {
            var active=session??pending??throw new CrsException("尚未建立安全会话。");
            using var c=active.Open(); var s=VaultAuthenticationStore.Read(c,null,envelope!);
            try {return s.PhoneVerificationEnabled==1;} finally {VaultAuthenticationStore.Clear(s);}
        }}
    }
    /// <summary>切换登录保护需要已授权会话、主密码和新手机代码，成功后写入审计。</summary>
    public Task SetPhoneVerificationAsync(bool enabled,string masterPassword,string code)
    {
        VaultSession expected;
        lock(gate) expected=Required();
        return Task.Run(()=>
        {
            lock(gate)
            {
            // 设置任务绑定发起时的会话，不能在锁定后借新的解锁会话修改安全策略。
            if(!ReferenceEquals(session,expected)) throw new CrsException("原安全会话已失效，请重新打开设置。");
            var active=Required(); using var proof=VaultKeyManager.Unlock(envelope!,masterPassword);
            VaultAuthenticationStore.Consume(active,envelope!,code,Now());
            using var c=active.Open(); using var tx=c.BeginTransaction(); var s=VaultAuthenticationStore.Read(c,tx,envelope!);
            try
            {
                if(s.SecurityVersion==1)
                {
                    // 仅升级已验证的加密认证表；旧程序会拒绝版本二，避免忽略已保存的开关。
                    c.Execute("ALTER TABLE SecuritySettings ADD COLUMN PhoneVerificationEnabled INTEGER NOT NULL DEFAULT 1 CHECK(PhoneVerificationEnabled IN (0,1));",transaction:tx);
                }
                c.Execute("UPDATE SecuritySettings SET SecurityVersion=2,PhoneVerificationEnabled=@value WHERE Id=1",new {value=enabled?1:0},tx);
                VaultAuthenticationStore.Event(c,tx,envelope!,"PhoneVerificationSettingChanged",enabled?"Enabled":"Disabled",Now()); tx.Commit();
            }
            finally {VaultAuthenticationStore.Clear(s);}
            }
        });
    }
}
