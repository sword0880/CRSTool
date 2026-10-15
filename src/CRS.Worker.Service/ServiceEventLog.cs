using System.Text.Json;
namespace CRS.Worker.Service;

/// <summary>受限目录内保存有界结构化诊断，仅记录固定分类，完整执行信息以加密库为准。</summary>
public sealed class ServiceEventLog(string dataDirectory)
{
    private readonly object gate = new();
    private bool reportedFailure;
    public void Write(string category, string job, string result)
    {
        lock (gate)
        {
            try
            {
                var directory = Path.Combine(dataDirectory, "logs"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "service-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
                // 每天最多 5 MiB，最多保留 14 个文件，避免无人值守日志占满系统磁盘。
                foreach (var old in Directory.EnumerateFiles(directory, "service-????????.jsonl").OrderDescending().Skip(13))
                    if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
                if (File.Exists(path) && new FileInfo(path).Length >= 5 * 1024 * 1024) return;
                File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, category, job, result }) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 诊断磁盘异常不能跳过 Quartz 停止和数据库释放；只输出一次固定分类。
                if (!reportedFailure) { reportedFailure = true; Console.Error.WriteLine("CRS_BACKGROUND_LOG_WRITE_FAILED"); }
            }
        }
    }
}
