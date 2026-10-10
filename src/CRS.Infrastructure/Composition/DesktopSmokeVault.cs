using System.Security.Cryptography;

namespace CRS.Infrastructure;

/// <summary>冒烟只创建随机隔离目录的合成库，走完整绑定验证，不提供已有库的认证旁路。</summary>
public static class DesktopSmokeVault
{
    public static async Task<VaultManager> CreateAsync(string temporaryRoot)
    {
        var directory=Path.Combine(Path.GetFullPath(temporaryRoot),"synthetic-vault-"+Guid.NewGuid().ToString("N"));
        var vault=new VaultManager();
        try
        {
            // 组合根同步等待时不捕获 WPF 上下文，避免启动线程与异步续体互相等待。
            _=await vault.CreateAsync(directory,Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
            var image=await vault.BeginEnrollmentAsync().ConfigureAwait(false); CryptographicOperations.ZeroMemory(image.Png);
            _=await vault.ConfirmEnrollmentAsync(vault.CodeForTest(true)).ConfigureAwait(false);
            await vault.CompleteEnrollmentAsync(true).ConfigureAwait(false); return vault;
        }
        catch {vault.Dispose(); throw;}
    }
}
