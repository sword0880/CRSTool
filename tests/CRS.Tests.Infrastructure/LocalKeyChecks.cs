using System.Security.Cryptography;
using System.Text;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;

internal static class LocalKeyChecks
{
    /// <summary>验证真实 Windows DPAPI：第一次主密码、日常仅手机、本机槽损坏／丢失与便携恢复。</summary>
    public static async Task RunAsync(string root)
    {
        const string password="Synthetic-Local-Key-Password-2026";
        long now=1_900_000_000; var path=Path.Combine(root,"local-key-vault");
        using var vault=new VaultManager {Clock=()=>now}; var created=await vault.CreateAsync(path,password);
        var deviceFile=Path.Combine(path,"vault.device");
        if(vault.HasLocalKey(path) || File.Exists(deviceFile)) throw new Exception("首次手机认证前已写入本机密钥。");
        _=await vault.BeginEnrollmentAsync(); _=await vault.ConfirmEnrollmentAsync(vault.CodeForTest(true));
        if(File.Exists(deviceFile) || vault.IsUnlocked) throw new Exception("未确认保存恢复凭证就允许本机登录。");
        await vault.CompleteEnrollmentAsync(true);
        var originalDevice=File.ReadAllBytes(deviceFile);
        if(!vault.HasLocalKey(path) || Encoding.UTF8.GetString(originalDevice).Contains(password) || Encoding.UTF8.GetString(originalDevice).Contains(created.RecoveryKey)) throw new Exception("本机槽不可用或泄漏恢复秘密。");
        var originalRepository=vault.Repository; var firstCode=vault.CodeForTest(); vault.Lock();
        await vault.BeginLocalUnlockAsync(path); RequireRestricted(vault);
        await Reject(()=>vault.VerifyTotpAsync(firstCode)); vault.Lock(); now+=30;
        await vault.BeginLocalUnlockAsync(path); RequireRestricted(vault); await vault.VerifyTotpAsync(vault.CodeForTest());
        try {originalRepository.History(); throw new Exception("本机登录使旧业务会话复活。");} catch(CrsException){}
        var backup=Path.Combine(root,"local-key.crsbak"); await vault.BackupAsync(backup); vault.Lock();
        // 校验 DPAPI 密文完整性与身份绑定，不把损坏本机槽降级为业务放行。
        var slot=System.Text.Json.Nodes.JsonNode.Parse(originalDevice)!.AsObject();
        var protectedKey=Convert.FromBase64String(slot["ProtectedKey"]!.GetValue<string>()); protectedKey[^1]^=1; slot["ProtectedKey"]=Convert.ToBase64String(protectedKey);
        File.WriteAllText(deviceFile,slot.ToJsonString()); if(vault.HasLocalKey(path)) throw new Exception("损坏 DPAPI 槽仍被接受。");
        await Reject(()=>vault.BeginLocalUnlockAsync(path)); RequireRestricted(vault);
        now+=30; await vault.UnlockAsync(path,password); RequireRestricted(vault); await vault.VerifyTotpAsync(vault.CodeForTest());
        if(!vault.HasLocalKey(path)) throw new Exception("主密码与手机重新认证后未修复本机槽。"); vault.Lock();
        File.Delete(deviceFile); await Reject(()=>vault.BeginLocalUnlockAsync(path));
        now+=30; await vault.UnlockAsync(path,password); await vault.VerifyTotpAsync(vault.CodeForTest()); vault.Lock();
        // 备份不携带 Windows 绑定槽，换机恢复仍能依靠原主密码／独立恢复密钥。
        using var restored=new VaultManager {Clock=()=>now}; var target=Path.Combine(root,"local-key-portable");
        await restored.RestoreAsync(backup,target,created.RecoveryKey,true,password);
        if(restored.HasLocalKey(target) || File.Exists(Path.Combine(target,"vault.device"))) throw new Exception("便携备份继承了本机 Windows 凭证。");
        await Reject(()=>restored.BeginLocalUnlockAsync(target)); await restored.UnlockAsync(target,password); RequireRestricted(restored);
        now+=30; await restored.VerifyTotpAsync(restored.CodeForTest()); restored.Lock(); now+=30;
        await restored.BeginLocalUnlockAsync(target); RequireRestricted(restored); await restored.VerifyTotpAsync(restored.CodeForTest());
        // 身份不匹配的本机槽不能串保险库，即使同一个 Windows 用户可解封原槽。
        restored.Lock(); var foreign=System.Text.Json.Nodes.JsonNode.Parse(originalDevice)!.AsObject(); foreign["VaultId"]=Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(target,"vault.device"),foreign.ToJsonString()); if(restored.HasLocalKey(target)) throw new Exception("混搭保险库身份未被阻断。");
        await Reject(()=>restored.BeginLocalUnlockAsync(target)); RequireRestricted(restored);
        // 未初始化保险库、旧明文目录均不能走本机便利登录。
        var partial=Path.Combine(root,"local-key-pending"); await vault.CreateAsync(partial,password); vault.Lock(); await Reject(()=>vault.BeginLocalUnlockAsync(partial));
        Console.WriteLine("本机手机登录通过：真实 DPAPI、首次双确认前不记密钥、无需主密码准备手机验证、验证码重放拒绝、旧会话失效、损坏／丢失／身份混搭拒绝、主密码重新启用、便携备份排除本机槽、恢复后重新绑定本机登录。");
    }
    private static void RequireRestricted(VaultManager vault)
    {
        if(vault.IsUnlocked) throw new Exception("本机槽解封被当作已认证。");
        try {_=vault.Repository; throw new Exception("仅凭本机槽可访问业务。");} catch(CrsException){}
    }
    private static async Task Reject(Func<Task> action) {try {await action();} catch(CrsException) {return;} throw new Exception("应拒绝的本机登录被接受。");}
}
