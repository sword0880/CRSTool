namespace CRS.Application.Abstractions;

public enum VaultState { Missing, Encrypted, PlaintextRejected, Invalid }
public sealed record VaultCreationInfo(string RecoveryKey);
// 认证状态与短暂展示素材通过端口传递，不含数据库密钥或原生连接。
public enum AuthenticationState { Locked, PendingEnrollment, PendingMfa, AwaitingRecoveryConfirmation, Authenticated }
public sealed record EnrollmentImage(byte[] Png);
/// <summary>安全用例端口不暴露 DEK、SQLCipher 或密码学类型；恢复只写入新的空目录。</summary>
public interface IVaultService : IDisposable
{
    bool IsUnlocked { get; }
    bool PhoneVerificationEnabled { get; }
    AuthenticationState Authentication { get; }
    VaultState Inspect(string directory);
    // 本机受保护凭证只用于准备手机验证，不代表已经通过业务认证。
    bool HasLocalKey(string directory);
    Task BeginLocalUnlockAsync(string directory);
    Task SetPhoneVerificationAsync(bool enabled,string masterPassword,string code);
    Task<VaultCreationInfo> CreateAsync(string directory,string password);
    Task UnlockAsync(string directory,string password);
    Task<EnrollmentImage> BeginEnrollmentAsync();
    Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(string code);
    Task CompleteEnrollmentAsync(bool recoverySaved);
    Task VerifyTotpAsync(string code);
    Task RecoverMfaAsync(string recoveryCode,string masterPassword);
    Task BeginPhoneReplacementAsync(string password,string code);
    Task ResetPasswordAsync(string directory,string recoveryKey,string newPassword);
    Task ChangePasswordAsync(string oldPassword,string newPassword,string code);
    Task BackupAsync(string path);
    Task RestoreAsync(string backupPath,string targetDirectory,string secret,bool useRecovery,string newPassword);
    void Lock();
}
