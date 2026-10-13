using CRS.Application.Abstractions;
using CRS.Infrastructure;
using Dapper;

internal static class WindowsHelloChecks
{
    /// <summary>使用可信端口替身验证策略和真实加密存储；不把替身测试当作系统生物识别验收。</summary>
    public static async Task RunAsync(string root)
    {
        const string password="Synthetic-Hello-Password-2026";
        const string changed="Synthetic-Hello-Changed-2026";
        var verifier=new Verifier(); var path=Path.Combine(root,"hello-vault");
        using var vault=new VaultManager(verifier); var created=await vault.CreateAsync(path,password);
        _=await vault.BeginEnrollmentAsync();
        if(!vault.CanChooseWindowsHello || vault.UsesWindowsHello) throw new Exception("首次默认方式不是 TOTP。");
        await Reject(()=>vault.EnrollWindowsHelloAsync(false));
        verifier.Failure=new OperationCanceledException(); await Reject(()=>vault.EnrollWindowsHelloAsync(true));
        verifier.Failure=new CrsException("合成：不可用"); await Reject(()=>vault.EnrollWindowsHelloAsync(true));
        if(vault.IsUnlocked || vault.UsesWindowsHello || vault.HasLocalKey(path)) throw new Exception("Hello 取消或失败提交了绑定。");
        verifier.Failure=null; await vault.EnrollWindowsHelloAsync(true);
        using(var c=vault.OpenForTest())
        {
            if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM SecuritySettings WHERE SecurityVersion=3 AND LoginMethod='WindowsHello' AND TotpSecret IS NULL AND PendingTotpSecret IS NULL AND InitializationComplete=1")!=1
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM RecoveryCodes")!=0) throw new Exception("Hello 留下 TOTP 秘密或恢复码。");
        }
        await Reject(()=>vault.SetPhoneVerificationAsync(false,password,"123456"));
        await vault.ChangePasswordAsync(password,changed,"");
        var old=vault.Repository; vault.Lock(); await vault.BeginLocalUnlockAsync(path);
        if(vault.IsUnlocked || vault.Authentication!=AuthenticationState.PendingWindowsHello || vault.CanChooseWindowsHello) throw new Exception("日常 Hello 被本机密钥绕过。");
        await Reject(()=>vault.VerifyTotpAsync("123456")); await Reject(()=>vault.BeginEnrollmentAsync());
        verifier.Failure=new CrsException("合成验证失败"); await Reject(vault.VerifyWindowsHelloAsync);
        if(vault.IsUnlocked) throw new Exception("失败 Hello 被放行。"); verifier.Failure=null;
        await vault.VerifyWindowsHelloAsync();
        try {old.History(); throw new Exception("旧仓储复活。");} catch(CrsException) { }
        var backup=Path.Combine(root,"hello.crsbak"); await vault.BackupAsync(backup);
        // 即使系统提示晚于锁屏返回成功，旧请求也必须失效。
        vault.Lock(); await vault.BeginLocalUnlockAsync(path);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifier.Wait=async()=>{started.SetResult(); await release.Task;};
        var late=vault.VerifyWindowsHelloAsync(); await started.Task; vault.Lock(); release.SetResult(); await Reject(()=>late);
        if(vault.IsUnlocked) throw new Exception("锁屏后的迟到 Hello 重新解锁。"); verifier.Wait=null;
        // Windows 身份改变不能只凭旧本机槽登记；主密码证明后才可显式重新验证。
        verifier.UserIdentity="S-1-5-21-SYNTHETIC-SECOND";
        await vault.BeginLocalUnlockAsync(path); await Reject(vault.VerifyWindowsHelloAsync); vault.Lock();
        await vault.UnlockAsync(path,changed); await vault.VerifyWindowsHelloAsync(); vault.Lock();
        await vault.ResetPasswordAsync(path,created.RecoveryKey,password);
        if(!vault.UsesWindowsHello || vault.Authentication!=AuthenticationState.PendingWindowsHello) throw new Exception("恢复密钥使 Hello 降级为 TOTP。");
        await vault.VerifyWindowsHelloAsync(); vault.Lock();
        using var restored=new VaultManager(verifier); var target=Path.Combine(root,"hello-restored");
        await restored.RestoreAsync(backup,target,changed,false,password);
        if(restored.HasLocalKey(target)) throw new Exception("备份带入本机槽。");
        await restored.UnlockAsync(target,password); await restored.VerifyWindowsHelloAsync();
        // 已启用 TOTP 或确认中的 TOTP 均不能再显示/接受首次 Hello 选择。
        using var totp=new VaultManager(verifier); await totp.CreateAsync(Path.Combine(root,"hello-totp"),password);
        _=await totp.BeginEnrollmentAsync(); _=await totp.ConfirmEnrollmentAsync(totp.CodeForTest(true));
        await Reject(()=>totp.EnrollWindowsHelloAsync(true)); await totp.CompleteEnrollmentAsync(true); totp.Lock();
        await totp.BeginLocalUnlockAsync(Path.Combine(root,"hello-totp"));
        if(totp.CanChooseWindowsHello) throw new Exception("已有 TOTP 出现 Hello 降级入口。");
        await Reject(totp.VerifyWindowsHelloAsync); await Reject(()=>totp.EnrollWindowsHelloAsync(true));
        Console.WriteLine("Windows Hello 策略通过：默认 TOTP、首次明确选择、恢复确认、取消/失败、方式隔离、日常本机验证、改密、锁屏迟到、Windows 身份变化及加密备份恢复。");
    }
    private static async Task Reject(Func<Task> action)
    {
        try {await action();} catch(Exception ex) when(ex is CrsException or OperationCanceledException) {return;}
        throw new Exception("应拒绝的 Hello 操作被允许。");
    }
    private sealed class Verifier : IWindowsHelloVerifier
    {
        public string UserIdentity {get;set;}="S-1-5-21-SYNTHETIC-FIRST";
        public Exception? Failure {get;set;}
        public Func<Task>? Wait {get;set;}
        public async Task VerifyAsync(CancellationToken cancellation=default)
        {
            if(Wait is not null) await Wait();
            if(Failure is not null) throw Failure;
            // 故意不检查取消，证明管理器本身阻断迟到成功结果。
        }
    }
}
