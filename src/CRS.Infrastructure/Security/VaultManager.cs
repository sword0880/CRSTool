using CRS.Application.Abstractions;
using CRS.Application.Performance;

namespace CRS.Infrastructure;

/// <summary>数据库无关入口；同一会话所有存储能力由启动时选择的适配器提供。</summary>
public sealed partial class VaultManager : IVaultStorageBackend
{
    private readonly IVaultStorageBackend backend;
    public string DatabaseProvider {get;}
    public VaultManager(IWindowsHelloVerifier? hello=null,string databaseProvider=VaultStorageProviders.DefaultProvider,
        VaultStorageProviders? providers=null)
    {
        backend=(providers??new VaultStorageProviders()).Create(databaseProvider,hello);
        DatabaseProvider=databaseProvider;
    }
    public bool IsUnlocked=>backend.IsUnlocked;
    public string DocumentEncryptionPassword=>backend.DocumentEncryptionPassword;
    public Task SetDocumentEncryptionPasswordAsync(string password)=>backend.SetDocumentEncryptionPasswordAsync(password);
    public VaultBackupCapacity? BackupCapacity=>backend.BackupCapacity;
    public ICalculationRepository Repository=>backend.Repository;
    public bool PhoneVerificationEnabled=>backend.PhoneVerificationEnabled;
    public AuthenticationState Authentication=>backend.Authentication;
    public bool UsesWindowsHello=>backend.UsesWindowsHello;
    public bool CanChooseWindowsHello=>backend.CanChooseWindowsHello;
    public Task<bool> IsWindowsHelloAvailableAsync()=>backend.IsWindowsHelloAvailableAsync();
    public Task SetLoginMethodAsync(bool useWindowsHello,string password,string code)=>backend.SetLoginMethodAsync(useWindowsHello,password,code);
    public Task EnrollWindowsHelloAsync(bool recoverySaved)=>backend.EnrollWindowsHelloAsync(recoverySaved);
    public Task VerifyWindowsHelloAsync()=>backend.VerifyWindowsHelloAsync();
    public VaultState Inspect(string directory)=>backend.Inspect(directory);
    public bool HasLocalKey(string directory)=>backend.HasLocalKey(directory);
    public Task BeginLocalUnlockAsync(string directory)=>backend.BeginLocalUnlockAsync(directory);
    public Task SetPhoneVerificationAsync(bool enabled,string masterPassword,string code)=>backend.SetPhoneVerificationAsync(enabled,masterPassword,code);
    public Task<VaultCreationInfo> CreateAsync(string directory,string password)=>backend.CreateAsync(directory,password);
    public Task UnlockAsync(string directory,string password)=>backend.UnlockAsync(directory,password);
    public Task<EnrollmentImage> BeginEnrollmentAsync()=>backend.BeginEnrollmentAsync();
    public Task<IReadOnlyList<string>> ConfirmEnrollmentAsync(string code)=>backend.ConfirmEnrollmentAsync(code);
    public Task CompleteEnrollmentAsync(bool recoverySaved)=>backend.CompleteEnrollmentAsync(recoverySaved);
    public Task VerifyTotpAsync(string code)=>backend.VerifyTotpAsync(code);
    public Task RecoverMfaAsync(string recoveryCode,string masterPassword)=>backend.RecoverMfaAsync(recoveryCode,masterPassword);
    public Task BeginPhoneReplacementAsync(string password,string code)=>backend.BeginPhoneReplacementAsync(password,code);
    public Task ResetPasswordAsync(string directory,string recoveryKey,string newPassword)=>backend.ResetPasswordAsync(directory,recoveryKey,newPassword);
    public Task ChangePasswordAsync(string oldPassword,string newPassword,string code)=>backend.ChangePasswordAsync(oldPassword,newPassword,code);
    public Task BackupAsync(string path,CancellationToken cancellation=default)=>backend.BackupAsync(path,cancellation);
    public Task RestoreAsync(string backupPath,string targetDirectory,string secret,bool useRecovery,string newPassword,CancellationToken cancellation=default)=>backend.RestoreAsync(backupPath,targetDirectory,secret,useRecovery,newPassword,cancellation);
    public IPerformanceUseCases CreatePerformanceUseCases(bool online=true)=>backend.CreatePerformanceUseCases(online);
    public Action<Action> CaptureGuard()=>backend.CaptureGuard();
    public void Execute(Action action)=>backend.Execute(action);
    public PerformanceDocument PrepareSharedPerformance(FrozenInput input,string broker,CancellationToken cancellation,Action<Action> originalSession,BrokerSourceDocument? sourceFacts=null)=>backend.PrepareSharedPerformance(input,broker,cancellation,originalSession,sourceFacts);
    public void CommitCalculation(CalculationResult result,IReadOnlyList<PerformanceDocument> documents,Action<Action> originalSession,CancellationToken token)=>backend.CommitCalculation(result,documents,originalSession,token);
    public void Lock()=>backend.Lock();
    public void Dispose()=>backend.Dispose();
}
