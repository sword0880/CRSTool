using CRS.Application.Abstractions;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;

internal static class MfaChecks
{
    /// <summary>使用可控测试时钟验证受限会话、防重放事务、绑定恢复及持久冷却。</summary>
    public static async Task RunAsync(string root)
    {
        const string password="Synthetic-Mfa-Password-2026";
        var path=Path.Combine(root,"mfa-vault"); long now=1_800_000_000;
        using var vault=new VaultManager {Clock=()=>now}; var created=await vault.CreateAsync(path,password);
        AssertRestricted(vault); var image=await vault.BeginEnrollmentAsync();
        if(image.Png.Length<100 || image.Png[0]!=137 || image.Png[1]!=80) throw new Exception("未生成内存 PNG 二维码。");
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(image.Png);
        var codes=await vault.ConfirmEnrollmentAsync(vault.CodeForTest(true)); AssertRestricted(vault);
        await Reject(()=>vault.CompleteEnrollmentAsync(false)); await vault.CompleteEnrollmentAsync(true);
        if(codes.Count!=8 || codes.Distinct().Count()!=8 || codes.Any(c=>c.Length!=32)) throw new Exception("手机恢复码熵或数量错误。");
        using(var c=vault.OpenForTest())
        {
            if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM RecoveryCodes WHERE length(CodeSalt)=16 AND length(CodeHash)=32 AND UsedAtUtc IS NULL")!=8) throw new Exception("数据库未保存带盐恢复码哈希。");
        }
        var consumedCode=vault.CodeForTest(); vault.Lock(); await vault.UnlockAsync(path,password); AssertRestricted(vault);
        await Reject(()=>vault.VerifyTotpAsync(consumedCode)); AssertRestricted(vault);
        vault.Lock(); now+=30; await vault.UnlockAsync(path,password); await vault.VerifyTotpAsync(vault.CodeForTest());
        var oldRepository=vault.Repository; vault.Lock(); now+=30;
        // 两个管理器模拟独立并发认证，防重放必须由数据库条件更新保障。
        using var concurrent=new VaultManager {Clock=()=>now}; await vault.UnlockAsync(path,password); await concurrent.UnlockAsync(path,password);
        var shared=vault.CodeForTest(); var outcomes=await Task.WhenAll(TryVerify(vault,shared),TryVerify(concurrent,shared));
        if(outcomes.Count(x=>x)!=1) throw new Exception("同一步骤并发消费不是恰好一次。");
        vault.Lock(); concurrent.Lock(); now+=30; await vault.UnlockAsync(path,password); await vault.VerifyTotpAsync(vault.CodeForTest());
        try {oldRepository.History(); throw new Exception("旧仓储借新 MFA 会话复活。");} catch(CrsException){}
        // 更换手机未验证首码就取消，应保留原绑定而非把旧手机自动吊销。
        now+=30; await vault.BeginPhoneReplacementAsync(password,vault.CodeForTest()); AssertRestricted(vault);
        var replacement=await vault.BeginEnrollmentAsync(); System.Security.Cryptography.CryptographicOperations.ZeroMemory(replacement.Png); vault.Lock();
        now+=30; await vault.UnlockAsync(path,password);
        if(vault.Authentication!=AuthenticationState.PendingMfa) throw new Exception("取消替换导致旧绑定失效。");
        await vault.VerifyTotpAsync(vault.CodeForTest()); vault.Lock(); now+=30;
        await vault.UnlockAsync(path,password); await vault.RecoverMfaAsync(codes[0]); AssertRestricted(vault); vault.Lock();
        await vault.UnlockAsync(path,password);
        if(vault.Authentication!=AuthenticationState.PendingEnrollment) throw new Exception("手机恢复中断后绕过强制绑定。");
        var rotated=await TestStores.Enroll(vault); vault.Lock(); now+=30; await vault.UnlockAsync(path,password);
        await Reject(()=>vault.RecoverMfaAsync(codes[0])); AssertRestricted(vault); vault.Lock();
        await vault.ResetPasswordAsync(path,created.RecoveryKey,password); AssertRestricted(vault); await TestStores.Enroll(vault);
        // 同一数据库已消费步骤，时间回退不能放行；快照回滚的边界另在用户说明中公开。
        vault.Lock(); now-=60; await vault.UnlockAsync(path,password); await Reject(()=>vault.VerifyTotpAsync(vault.CodeForTest())); vault.Lock();
        now+=120;
        await vault.UnlockAsync(path,password); await vault.VerifyTotpAsync(vault.CodeForTest()); vault.Lock();
        for(var i=0;i<5;i++)
        {
            using var attempt=new VaultManager {Clock=()=>now}; await attempt.UnlockAsync(path,password);
            await Reject(()=>attempt.VerifyTotpAsync("abcdef")); AssertRestricted(attempt);
        }
        using(var cooled=new VaultManager {Clock=()=>now})
        {
            await cooled.UnlockAsync(path,password); await Reject(()=>cooled.VerifyTotpAsync(cooled.CodeForTest())); AssertRestricted(cooled);
        }
        now+=901; using(var expired=new VaultManager {Clock=()=>now})
        {
            await expired.UnlockAsync(path,password); await expired.VerifyTotpAsync(expired.CodeForTest());
            using var c=expired.OpenForTest(); if(c.ExecuteScalar<int>("SELECT FailedAttempts FROM SecuritySettings")!=0) throw new Exception("冷却到期成功验证未清除失败状态。");
        }
        // 取消首次初始化后仍必须重新绑定，不能只凭主密码打开业务。
        var partial=Path.Combine(root,"mfa-incomplete"); await vault.CreateAsync(partial,password); _=await vault.BeginEnrollmentAsync(); vault.Lock();
        await vault.UnlockAsync(partial,password); AssertRestricted(vault); await TestStores.Enroll(vault);
        Console.WriteLine("MFA 集成通过：首次绑定门槛、恢复确认、首码消费、并发防重放、旧仓储失效、换机取消、恢复码消费及旋转、忘记密码强制绑定、回退拒绝、跨进程冷却、冷却到期、初始化中断。");
    }
    private static void AssertRestricted(VaultManager vault)
    {
        if(vault.IsUnlocked) throw new Exception("MFA 未完成已经解锁。");
        try {_=vault.Repository; throw new Exception("认证未完成暴露业务仓储。");} catch(CrsException){}
    }
    private static async Task<bool> TryVerify(VaultManager vault,string code) {try {await vault.VerifyTotpAsync(code); return true;} catch(CrsException) {return false;}}
    private static async Task Reject(Func<Task> action) {try {await action();} catch(CrsException) {return;} throw new Exception("预期认证拒绝却成功。");}
}
