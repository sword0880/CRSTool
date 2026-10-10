using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using NLog;

internal static class SettingsChecks
{
    public static async Task RunAsync(string temporaryRoot)
    {
        var store = new DesktopSettingsStore(Path.Combine(temporaryRoot, "settings-check"));
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
        var previous = LogManager.Configuration;
        try
        {
            LocalStore.ConfigureLogging(chosen.LogDirectory, true, chosen.LogLevel, chosen.LogRetentionFiles, chosen.LogFileSizeMb, true);
            var logger = LogManager.GetLogger("settings-check"); logger.Info("SUPPRESSED_INFO"); logger.Warn("VISIBLE_WARNING"); LogManager.Flush();
            var log = string.Join("", Directory.GetFiles(chosen.LogDirectory, "*.log").Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream); return reader.ReadToEnd();
            }));
            if (!log.Contains("VISIBLE_WARNING") || log.Contains("SUPPRESSED_INFO")) throw new Exception("日志目录或级别未生效。");
            LocalStore.ConfigureLogging(chosen.LogDirectory, true, "Warn", 2, 1, true);
            var largeEntry = new string('x', 650000);
            for (var index = 0; index < 6; index++) logger.Warn(largeEntry);
            LogManager.Flush();
            var rotated = Directory.GetFiles(chosen.LogDirectory, "crs*.log");
            if (rotated.Length is < 2 or > 3) throw new Exception("日志未按大小轮转或归档上限未生效。");
            LocalStore.ConfigureLogging(chosen.LogDirectory, false);
            if (LogManager.Configuration!.LoggingRules.Count != 0) throw new Exception("关闭日志后仍有输出规则。");
        }
        finally { LogManager.Configuration = previous; }
        Console.WriteLine("设置验证通过：默认目录不回退旧库、保存不迁移数据、失败不覆盖、日志开关与级别／轮转（4 项）。");
    }
}
