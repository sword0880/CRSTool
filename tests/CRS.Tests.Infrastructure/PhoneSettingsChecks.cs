using System.Security.Cryptography;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;

internal static class PhoneSettingsChecks
{
    /// <summary>手机开关仅在加密库内持久化，默认开启、身份确认、关闭进入、重新启用与便携恢复。</summary>
    internal static async Task RunAsync(string root)
    {
        const string password="Synthetic-Phone-Setting-Password-2026";
        long now=1_950_000_000; var path=Path.Combine(root,"phone-settings");
        using var vault=new VaultManager {Clock=()=>now}; var created=await vault.CreateAsync(path,password);
        await Reject(()=>vault.SetPhoneVerificationAsync(false,password,"000000")); await TestStores.Enroll(vault);
        if(!vault.PhoneVerificationEnabled) throw new Exception("新库未默认开启手机验证。");
        now+=30; await Reject(()=>vault.SetPhoneVerificationAsync(false,"wrong-password",vault.CodeForTest()));
        if(!vault.PhoneVerificationEnabled) throw new Exception("错误主密码关闭了手机验证。");
        await Reject(()=>vault.SetPhoneVerificationAsync(false,password,"abcdef"));
        if(!vault.PhoneVerificationEnabled) throw new Exception("错误手机代码改变了开关。");
        await vault.SetPhoneVerificationAsync(false,password,vault.CodeForTest());
        using(var c=vault.OpenForTest())
        {
            if(vault.PhoneVerificationEnabled || c.ExecuteScalar<int>("SELECT SecurityVersion FROM SecuritySettings")!=2
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM SecurityEvents WHERE EventType='PhoneVerificationSettingChanged' AND Result='Disabled'")!=1) throw new Exception("关闭状态、版本升级或审计未持久化。");
        }
        var old=vault.Repository; var backup=Path.Combine(root,"phone-off.crsbak"); await vault.BackupAsync(backup); vault.Lock();
        using(var next=new VaultManager {Clock=()=>now})
        {
            await next.BeginLocalUnlockAsync(path);
            if(!next.IsUnlocked || next.PhoneVerificationEnabled) throw new Exception("重启后关闭状态未生效。");
            try {old.History(); throw new Exception("关闭验证使旧会话复活。");} catch(CrsException){}
            now+=30; await next.SetPhoneVerificationAsync(true,password,next.CodeForTest()); next.Lock();
            // 修改普通明文文件不能将加密库中的已启用策略降为关闭。
            File.WriteAllText(Path.Combine(path,"settings.json"),"{\"PhoneVerificationEnabled\":false}");
            await next.BeginLocalUnlockAsync(path);
            if(next.IsUnlocked || !next.PhoneVerificationEnabled) throw new Exception("启用状态被忽略或普通配置绕过手机验证。");
            now+=30; await next.VerifyTotpAsync(next.CodeForTest());
        }
        using var portable=new VaultManager {Clock=()=>now}; var target=Path.Combine(root,"phone-off-restored");
        await portable.RestoreAsync(backup,target,created.RecoveryKey,true,password); await portable.UnlockAsync(target,password);
        if(!portable.IsUnlocked || portable.PhoneVerificationEnabled) throw new Exception("便携恢复没有保留关闭策略或仍强制手机验证。");
        portable.Lock(); await portable.ResetPasswordAsync(target,created.RecoveryKey,password);
        if(portable.IsUnlocked) throw new Exception("未完成的密码恢复通过关闭开关放行。");
        await TestStores.Enroll(portable); using(var c=portable.OpenForTest()) c.Execute("UPDATE SecuritySettings SET SecurityVersion=999 WHERE Id=1");
        portable.Lock(); await Reject(()=>portable.BeginLocalUnlockAsync(target)); if(portable.IsUnlocked) throw new Exception("不支持版本降级为自动进入。");
        Console.WriteLine("手机开关通过：默认开启、主密码／新验证码确认、加密持久化及审计、关闭后本机进入、重新启用恢复验证、普通配置不可绕过、旧会话阻断、便携恢复保持策略、恢复中不放行、未知版本拒绝。");
    }
    private static async Task Reject(Func<Task> action) {try {await action();} catch(Exception ex) when(ex is CrsException or CryptographicException) {return;} throw new Exception("应拒绝的安全设置操作被接受。");}
}
