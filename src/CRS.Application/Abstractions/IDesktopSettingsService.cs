namespace CRS.Application.Abstractions;

/// <summary>保存本机日志与数据库设置；前台不直接读写设置文件。</summary>
public interface IDesktopSettingsService
{
    DesktopSettings Load();
    Task SaveAsync(DesktopSettings settings);
}

public sealed record DesktopSettings(string DatabaseDirectory, string LogDirectory,
    bool LoggingEnabled = true, string LogLevel = "Info", int LogRetentionFiles = 14, int LogFileSizeMb = 1);
