using System.Text.Json;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

/// <summary>本机设置原子保存，与数据库文件及券商配置分开管理。</summary>
public sealed class DesktopSettingsStore(string? localDataRoot = null) : IDesktopSettingsService
{
    private readonly string root = localDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string SettingsPath => Path.Combine(root, "CRS", "settings.json");

    public DesktopSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new(Path.Combine(root, "CRS", "data"), Path.Combine(root, "CRS", "logs"));
        }
        try { return Validate(JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath))
            ?? throw new CrsException("本机设置为空，请检查 CRS/settings.json。")); }
        catch (JsonException) { throw new CrsException("本机设置格式错误，请检查 CRS/settings.json。原数据库未被修改。"); }
    }

    public Task SaveAsync(DesktopSettings settings) => Task.Run(() =>
    {
        var validated = Validate(settings);
        CheckWritable(validated.DatabaseDirectory); CheckWritable(validated.LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(validated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    });

    private static DesktopSettings Validate(DesktopSettings settings)
    {
        static string DirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new CrsException("日志和数据库目录必须填写完整路径。");
            return Path.GetFullPath(path.Trim());
        }
        if (settings.LogLevel is not ("Debug" or "Info" or "Warn" or "Error")
            || settings.LogRetentionFiles is < 1 or > 365 || settings.LogFileSizeMb is < 1 or > 100)
            throw new CrsException("日志级别或轮转参数无效：保留 1—365 个文件，每个文件 1—100 MB。");
        return settings with { DatabaseDirectory = DirectoryPath(settings.DatabaseDirectory), LogDirectory = DirectoryPath(settings.LogDirectory) };
    }

    private static void CheckWritable(string path)
    {
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".crs-write-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }
}
