using System.Globalization;
using System.Text.Json;
using CRS.Timing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace CRS.Worker.Service;

/// <summary>受限目录内保存有界结构化诊断，仅记录固定分类，完整执行信息以加密库为准。</summary>
public sealed class ServiceEventLog(string dataDirectory, TimeProvider? clock = null) : IDisposable
{
    private const long MaxDailyBytes = 5 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string directory = Path.Combine(dataDirectory, "logs");
    private string? activePath;
    private Logger? logger;
    private bool reportedFailure;
    private bool disposed;

    public void Write(string category, string job, string result)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var time = BeijingClock.Normalize((clock ?? TimeProvider.System).GetUtcNow());
                var path = Path.Combine(directory, "service-" + time.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
                if (activePath != path) Open(path);
                if (new FileInfo(path).Length >= MaxDailyBytes) return;
                logger!.Information("{time} {category} {job} {result}", time, category, job, result);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Close();
                ReportFailure();
            }
        }
    }

    private void Open(string path)
    {
        Close();
        StorageSecurity.RejectLinks(path);
        Directory.CreateDirectory(directory);
        // 让写入失败回到宿主重试，同时只输出固定错误分类。
        logger = new LoggerConfiguration().AuditTo.File(new ServiceJsonFormatter(), path).CreateLogger();
        activePath = path;
    }

    private void ReportFailure()
    {
        if (reportedFailure) return;
        reportedFailure = true;
        Console.Error.WriteLine("CRS_BACKGROUND_LOG_WRITE_FAILED");
    }

    private void Close()
    {
        var previous = logger;
        logger = null;
        activePath = null;
        try { previous?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ReportFailure(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Close();
        }
    }

    private sealed class ServiceJsonFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            object? Value(string name) => ((ScalarValue)logEvent.Properties[name]).Value;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                time = Value("time"), category = Value("category"), job = Value("job"), result = Value("result")
            }));
        }
    }
}
