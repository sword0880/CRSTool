using System.Security.Cryptography;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;

internal static class PhoneSettingsChecks
{
    internal static async Task RunAsync(string root)
    {
        const string password="Synthetic-Phone-Setting-Password-2026";
        long now=1_950_000_000;var path=Path.Combine(root,"phone-settings");
        using var vault=new VaultManager {Clock=()=>now};await vault.CreateAsync(path,password);await TestStores.Enroll(vault);
        const string documentPassword="Synthetic-Document-Encryption-2026";
        await vault.SetDocumentEncryptionPasswordAsync(documentPassword);
        if(vault.DocumentEncryptionPassword!=documentPassword)throw new Exception("统一文档密码未写入加密库。");
        await Reject(()=>vault.SetPhoneVerificationAsync(false,password,"000000"));
        if(!vault.PhoneVerificationEnabled) throw new Exception("允许关闭全部验证方式。");
        // 模拟旧版已关闭手机验证的加密库，新版本必须重新要求手机代码。
        using(var c=vault.OpenForTest()) c.Execute("ALTER TABLE SecuritySettings ADD COLUMN PhoneVerificationEnabled INTEGER NOT NULL DEFAULT 1; UPDATE SecuritySettings SET SecurityVersion=2,PhoneVerificationEnabled=0 WHERE Id=1");
        var old=vault.Repository;vault.Lock();await vault.BeginLocalUnlockAsync(path);
        if(vault.IsUnlocked) throw new Exception("旧版关闭状态绕过了手机验证。");
        now+=30;await vault.VerifyTotpAsync(vault.CodeForTest());
        if(vault.DocumentEncryptionPassword!=documentPassword)throw new Exception("重新解锁后统一文档密码丢失。");
        try {old.History();throw new Exception("旧会话重新获得权限。");} catch(CrsException) {}
        now+=30;await Reject(()=>vault.SetLoginMethodAsync(false,"wrong-password",vault.CodeForTest()));
        await vault.SetLoginMethodAsync(false,password,vault.CodeForTest());
        using(var c=vault.OpenForTest()) if(c.ExecuteScalar<int>("SELECT PhoneVerificationEnabled FROM SecuritySettings")!=1) throw new Exception("未修复旧的关闭状态。");
        vault.Lock();await vault.UnlockAsync(path,password);
        if(vault.IsUnlocked) throw new Exception("主密码绕过了手机验证。");
        now+=30;await vault.VerifyTotpAsync(vault.CodeForTest());
        Console.WriteLine("验证方式通过：禁止全部关闭、旧设置不再免验证、主密码和新代码确认、旧会话失效。");
    }
    private static async Task Reject(Func<Task> action) {try {await action();} catch(Exception ex) when(ex is CrsException or CryptographicException) {return;} throw new Exception("应拒绝的安全设置操作被接受。");}
}
