using CRS.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>测试也必须显式创建加密保险库；合成密码不构成生产默认密码或明文兼容入口。</summary>
internal static class TestStores
{
    private static readonly Dictionary<string,VaultManager> managers=new();
    internal static LocalStore Create(string directory)
    {
        var vault=new VaultManager(); _=vault.CreateAsync(directory,"Synthetic-Test-Password-2026").GetAwaiter().GetResult();
        Enroll(vault).GetAwaiter().GetResult();
        managers[Path.GetFullPath(directory)]=vault; return (LocalStore)vault.Repository;
    }
    internal static SqliteConnection Open(LocalStore store)=>managers[store.DirectoryPath].OpenForTest();
    internal static async Task<IReadOnlyList<string>> Enroll(VaultManager vault)
    {
        var image=await vault.BeginEnrollmentAsync(); System.Security.Cryptography.CryptographicOperations.ZeroMemory(image.Png);
        var codes=await vault.ConfirmEnrollmentAsync(vault.CodeForTest(true)); await vault.CompleteEnrollmentAsync(true); return codes;
    }
    internal static async Task Authenticate(VaultManager vault)
    {
        if(vault.Authentication==CRS.Application.Abstractions.AuthenticationState.PendingEnrollment) {await Enroll(vault); return;}
        // 合成测试时钟推进一步，真实客户端始终使用系统 UTC 时钟。
        var now=vault.Clock(); vault.Clock=()=>now+30; await vault.VerifyTotpAsync(vault.CodeForTest());
    }
}
