using CRS.Application.Abstractions;

namespace CRS.DesktopClient.Services;

/// <summary>创建验证窗口前检查本机登录；是否放行由加密保险库决定。</summary>
public static class VaultStartup
{
    public static async Task<bool> TryEnterAsync(IVaultService vault,string directory)
    {
        var entered=false;
        try
        {
            // 普通配置不能关闭验证；完整初始化、本机密钥与加密库内策略必须同时有效。
            if(vault.Inspect(directory)!=VaultState.Encrypted || !vault.HasLocalKey(directory)) return false;
            await vault.BeginLocalUnlockAsync(directory).ConfigureAwait(false);
            entered=vault.IsUnlocked;
            return entered;
        }
        catch(Exception)
        {
            // 本机凭证或库读取失败时回到恢复入口，不自动重建、覆盖或放行。
            return false;
        }
        finally
        {
            // 仍需手机验证时清除预检上下文，由验证窗口建立自己的目录绑定会话。
            if(!entered) vault.Lock();
        }
    }
}
