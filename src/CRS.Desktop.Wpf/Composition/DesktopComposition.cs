using CRS.Application.Abstractions;
using System.IO;
using CRS.Infrastructure;
using NLog;
namespace CRS.DesktopClient.Composition;
/// <summary>桌面启动组合根，基础设施类型只在此处装配。</summary>
internal static class DesktopComposition
{
    public static DesktopRuntime Create(bool smoke)
    {
        var directory = smoke ? Path.Combine(Path.GetTempPath(),"CRS.DesktopClient-smoke",Guid.NewGuid().ToString("N")) : null;
        var settingsStore = new DesktopSettingsStore(directory);
        var settings = settingsStore.Load();
        var repository = new LocalStore(settings.DatabaseDirectory);
        LocalStore.ConfigureLogging(settings.LogDirectory, settings.LoggingEnabled, settings.LogLevel,
            settings.LogRetentionFiles, settings.LogFileSizeMb, exactLogDirectory: true);
        var operations = new DesktopOperations(repository,Path.Combine(AppContext.BaseDirectory,"config"));
        return new(new DesktopWorkflow(operations),operations,repository.DirectoryPath,settingsStore);
    }
    public static void LogStartupFailure(Exception error) => LogManager.GetCurrentClassLogger().Error("程序启动失败；类别={0}",error.GetType().Name);
    public static void Shutdown() => LogManager.Shutdown();
}
internal sealed record DesktopRuntime(IDesktopUseCases UseCases, IDesktopOperations Operations, string DirectoryPath,
    IDesktopSettingsService Settings);
