using CRS.Application.Abstractions;
using CRS.AI.Models;
using System.IO;
using CRS.Infrastructure;
using Microsoft.Extensions.Logging;
namespace CRS.DesktopClient.Composition;
/// <summary>桌面启动组合根，基础设施类型只在此处装配。</summary>
internal static class DesktopComposition
{
    private static readonly DesktopLogging Logging = new();
    private static readonly VaultStorageProviders StorageProviders = new(loggerFactory: Logging.Factory);

    public static IVaultService CreateVault()=>new VaultManager(new WindowsHelloVerifier(),VaultStorageProviders.ConfiguredProvider(),StorageProviders);
    public static IDesktopSettingsService CreateSettings(string? directory = null) => new DesktopSettingsStore(directory, settings =>
    {
        Logging.Configure(settings);
        Logging.Factory.CreateLogger("DesktopSettings").LogInformation("桌面日志设置已应用。");
    });

    public static DesktopRuntime Create(bool smoke,IVaultService? vault=null,IDesktopSettingsService? userSettings=null)
    {
        var directory = smoke ? Path.Combine(Path.GetTempPath(),"CRS.DesktopClient-smoke",Guid.NewGuid().ToString("N")) : null;
        var settingsStore = userSettings??CreateSettings(directory);
        var settings = settingsStore.Load();
        // 冒烟也使用独立加密库；生产启动必须先完成主密码解锁，不能自动创建空数据库。
        if(smoke) {var synthetic=DesktopSmokeVault.CreateAsync(directory!,StorageProviders).GetAwaiter().GetResult(); vault=synthetic; settings=settings with {DatabaseDirectory=((LocalStore)synthetic.Repository).DirectoryPath}; settingsStore.SaveAsync(settings).GetAwaiter().GetResult();}
        if(vault is null || !vault.IsUnlocked) throw new InvalidOperationException("保险库未解锁。");
        var manager=(VaultManager)vault;
        var repository = manager.Repository;
        Logging.Configure(settings);
        var officialRates = smoke ? null : new OfficialAnnualExchangeRates(CRS.DesktopClient.Services.WorkerExchangeRatesGateway.ReadAsync);
        var operations = new DesktopOperations(repository,Path.Combine(AppContext.BaseDirectory,"config"),manager,officialRates,Logging.Factory);
        // 冒烟使用临时配置目录，不读取真实 Windows 用户的模型密钥。
        return new(new DesktopWorkflow(operations,smoke?null:new CRS.DesktopClient.Services.WorkerNotificationSettingsGateway()),operations,settings.DatabaseDirectory,settingsStore,vault,manager.CreatePerformanceUseCases(!smoke),new AiModelService(directory));
    }
    public static void LogStartupFailure(Exception error) => Logging.Factory.CreateLogger(typeof(DesktopComposition).FullName!)
        .LogError("程序启动失败；类别={Category:l}", SensitiveDiagnostics.FailureCategory(error.GetType().Name));
    public static void Shutdown() => Logging.Dispose();
}
internal sealed record DesktopRuntime(IDesktopUseCases UseCases, IDesktopOperations Operations, string DirectoryPath,
    IDesktopSettingsService Settings,IVaultService Vault,CRS.Application.Performance.IPerformanceUseCases Performance,
    IAiModelService AiModels);
