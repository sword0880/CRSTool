using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Worker.Service;

/// <summary>后台并发与等待预算属于服务运维配置，不让插件自行扩大宿主资源。</summary>
public sealed record WorkerExecutionSettings(int MaxConcurrency = 8, int MaxPendingPerPlugin = 16)
{
    public int EffectiveMaxConcurrency => MaxConcurrency == 0 ? Math.Clamp(Environment.ProcessorCount, 4, 16) : MaxConcurrency;
    public WorkerExecutionSettings Validate()
    {
        if (MaxConcurrency is < 0 or > 32 || MaxPendingPerPlugin is < 1 or > 32)
            throw new InvalidDataException("后台并发或等待预算超出支持范围。");
        return this;
    }
    public static WorkerExecutionSettings Read(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "worker-settings.json");
        StorageSecurity.RejectLinks(path);
        if (!File.Exists(path)) return new();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > 4096) throw new InvalidDataException("后台运行配置超限。");
        return (JsonSerializer.Deserialize<WorkerExecutionSettings>(input, new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("后台运行配置为空。")).Validate();
    }
}
