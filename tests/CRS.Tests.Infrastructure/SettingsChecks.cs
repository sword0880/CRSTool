using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using Microsoft.Extensions.Logging;

internal static class SettingsChecks
{
    public static async Task RunAsync(string temporaryRoot)
    {
        using var logging = new DesktopLogging();
        var store = new DesktopSettingsStore(Path.Combine(temporaryRoot, "settings-check"), logging.Configure);
        var logger = logging.Factory.CreateLogger("settings-check");
        var defaults = store.Load();
        if (!defaults.DatabaseDirectory.EndsWith(Path.Combine("CRS", "data"))) throw new Exception("新安装数据目录错误。");
        var legacyRoot = Path.Combine(temporaryRoot, "legacy-check");
        Directory.CreateDirectory(Path.Combine(legacyRoot, "CRS.WinForms"));
        File.WriteAllBytes(Path.Combine(legacyRoot, "CRS.WinForms", "crs.db"), []);
        if (new DesktopSettingsStore(legacyRoot).Load().DatabaseDirectory != Path.Combine(legacyRoot, "CRS", "data")) throw new Exception("不应自动回退旧数据目录。");
        var chosen = new DesktopSettings(Path.Combine(temporaryRoot, "chosen-db"), Path.Combine(temporaryRoot, "chosen-logs"), true, "Warn", 7, 2);
        await store.SaveAsync(chosen);
        if (store.Load() != chosen || File.Exists(Path.Combine(chosen.DatabaseDirectory, "crs.db"))) throw new Exception("保存设置修改了当前数据库，或选项未持久化。");
        var oldText = File.ReadAllText(store.SettingsPath);
        try { await store.SaveAsync(chosen with { DatabaseDirectory = "relative", LogRetentionFiles = 0 }); throw new Exception("无效设置被保存。"); } catch (CrsException) { }
        if (File.ReadAllText(store.SettingsPath) != oldText) throw new Exception("失败的保存破坏了有效设置。");
        logger.LogInformation("SUPPRESSED_INFO");
        logger.LogWarning("VISIBLE_WARNING");
        var log = ReadLog(chosen.LogDirectory);
        if (!log.Contains("+08:00|Warn|VISIBLE_WARNING") || log.Contains("SUPPRESSED_INFO"))
            throw new Exception("日志目录、级别或北京时间未生效。");

        // 使用同一个记录器，验证保存设置即可切换级别、目录和开关。
        var live = chosen with { LogDirectory = Path.Combine(temporaryRoot, "live-logs"), LogLevel = "Debug" };
        await store.SaveAsync(live);
        logger.LogDebug("LIVE_DEBUG");
        if (!ReadLog(live.LogDirectory).Contains("LIVE_DEBUG") || ReadLog(chosen.LogDirectory).Contains("LIVE_DEBUG"))
            throw new Exception("保存后日志级别或目录未立即生效。");
        await store.SaveAsync(live with { LogLevel = "Error" });
        logger.LogWarning("LIVE_SUPPRESSED");
        logger.LogError("LIVE_ERROR");
        var liveText = ReadLog(live.LogDirectory);
        if (liveText.Contains("LIVE_SUPPRESSED") || !liveText.Contains("LIVE_ERROR"))
            throw new Exception("已有记录器未刷新日志级别。");
        await store.SaveAsync(live with { LoggingEnabled = false });
        logger.LogCritical("DISABLED_ERROR");
        if (logger.IsEnabled(LogLevel.Critical) || ReadLog(live.LogDirectory).Contains("DISABLED_ERROR"))
            throw new Exception("保存后关闭日志未立即生效。");

        await store.SaveAsync(chosen with { LogRetentionFiles = 2, LogFileSizeMb = 1 });
        var largeEntry = new string('x', 650000);
        for (var index = 0; index < 8; index++) logger.LogWarning("{Entry:l}", largeEntry);
        var rotated = Directory.GetFiles(chosen.LogDirectory, "crs*.log");
        if (rotated.Length != 3) throw new Exception("日志未按大小轮转或未保留两个归档加当前文件。");

        static string ReadLog(string directory)
        {
            using var input = new FileStream(Path.Combine(directory, "crs.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(input);
            return reader.ReadToEnd();
        }
        Console.WriteLine("设置验证通过：默认目录不回退旧库、保存不迁移数据、失败不覆盖、日志开关与级别／轮转（4 项）。");
    }
}
