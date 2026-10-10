using CRS.Application.Abstractions;
using System.IO;
using CRS.Infrastructure;
using NLog;
namespace CRS.DesktopClient.Composition;
/// <summary>桌面启动组合根，基础设施类型只在此处装配。</summary>
internal static class DesktopComposition
{
    public static IVaultService CreateVault()=>new VaultManager();
    public static IDesktopSettingsService CreateSettings()=>new DesktopSettingsStore();
    public static DesktopRuntime Create(bool smoke,IVaultService? vault=null,IDesktopSettingsService? userSettings=null)
    {
        var directory = smoke ? Path.Combine(Path.GetTempPath(),"CRS.DesktopClient-smoke",Guid.NewGuid().ToString("N")) : null;
        var settingsStore = userSettings??new DesktopSettingsStore(directory);
        var settings = settingsStore.Load();
        // 冒烟也使用独立加密库；生产启动必须先完成主密码解锁，不能自动创建空数据库。
        if(smoke) {var synthetic=DesktopSmokeVault.CreateAsync(directory!).GetAwaiter().GetResult(); vault=synthetic; settings=settings with {DatabaseDirectory=((LocalStore)synthetic.Repository).DirectoryPath}; settingsStore.SaveAsync(settings).GetAwaiter().GetResult();}
        if(vault is null || !vault.IsUnlocked) throw new InvalidOperationException("保险库未解锁。");
        var manager=(VaultManager)vault;
        var repository = manager.Repository;
        LocalStore.ConfigureLogging(settings.LogDirectory, settings.LoggingEnabled, settings.LogLevel,
            settings.LogRetentionFiles, settings.LogFileSizeMb, exactLogDirectory: true);
        var operations = new DesktopOperations(repository,Path.Combine(AppContext.BaseDirectory,"config"),manager);
        return new(new DesktopWorkflow(operations),operations,settings.DatabaseDirectory,settingsStore,vault);
    }
    public static void LogStartupFailure(Exception error) => LogManager.GetCurrentClassLogger().Error("程序启动失败；类别={0}",error.GetType().Name);
    public static void Shutdown() => LogManager.Shutdown();
}
internal sealed record DesktopRuntime(IDesktopUseCases UseCases, IDesktopOperations Operations, string DirectoryPath,
    IDesktopSettingsService Settings,IVaultService Vault);
