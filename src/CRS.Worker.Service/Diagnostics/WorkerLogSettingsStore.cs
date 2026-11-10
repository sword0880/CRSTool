using CRS.IO;
using CRS.Maintenance;

namespace CRS.Worker.Service;

internal sealed class WorkerLogSettingsStore(string directory, byte[] key)
{
    private const string Purpose = "worker-log-settings/v1";
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, "worker-log-settings.json");
    private WorkerLogSettings? current;

    internal WorkerLogSettings Read()
    {
        lock (gate) return current ??= ReadFile();
    }

    private WorkerLogSettings ReadFile()
    {
        try
        {
            var saved = AuthenticatedJsonFile.Read<WorkerLogSettings>(path, key, Purpose);
            return WorkerLogSettings.IsValidRetention(saved.RetentionDays) && saved.Revision > 0 ? saved
                : throw new InvalidDataException("后台日志设置无效。");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
    }

    internal WorkerLogSettings? Save(WorkerLogSettings requested)
    {
        if (!WorkerLogSettings.IsValidRetention(requested.RetentionDays) || requested.Revision < 0)
            throw new ArgumentException("后台日志保留天数或修订无效。", nameof(requested));
        lock (gate)
        {
            var saved = Read();
            if (saved.Revision != requested.Revision) return null;
            if (saved.RetentionDays == requested.RetentionDays) return saved;
            var next = requested with { Revision = checked(saved.Revision + 1) };
            AuthenticatedJsonFile.Write(path, key, Purpose, next);
            current = next;
            return next;
        }
    }
}
