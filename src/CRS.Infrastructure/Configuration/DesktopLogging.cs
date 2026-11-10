using System.Globalization;
using CRS.Application.Abstractions;
using CRS.Timing;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;

namespace CRS.Infrastructure;

/// <summary>桌面进程拥有日志生命周期；保存设置时已有记录器继续写入当前输出端。</summary>
public sealed class DesktopLogging : IDisposable
{
    private readonly ReloadableFile file = new();

    public DesktopLogging()
    {
        var logger = new LoggerConfiguration().MinimumLevel.ControlledBy(file.Level).WriteTo.Sink(file).CreateLogger();
        Factory = new SerilogLoggerFactory(logger, dispose: true);
    }

    public ILoggerFactory Factory { get; }

    public void Configure(DesktopSettings settings) => file.Configure(settings);

    public void Dispose() => Factory.Dispose();

    private sealed class ReloadableFile : ILogEventSink, IDisposable
    {
        private const LogEventLevel Off = (LogEventLevel)((int)LogEventLevel.Fatal + 1);
        private readonly object gate = new();
        private Logger? current;
        private bool disposed;

        public LoggingLevelSwitch Level { get; } = new(Off);

        public void Configure(DesktopSettings settings)
        {
            lock (gate)
            {
                if (disposed) return;
                var minimum = settings.LogLevel switch
                {
                    "Debug" => LogEventLevel.Debug,
                    "Info" => LogEventLevel.Information,
                    "Warn" => LogEventLevel.Warning,
                    "Error" => LogEventLevel.Error,
                    _ => throw new ArgumentOutOfRangeException(nameof(settings))
                };
                var next = settings.LoggingEnabled
                    ? new LoggerConfiguration().MinimumLevel.Is(minimum)
                        .WriteTo.File(new DesktopTextFormatter(), Path.Combine(settings.LogDirectory, "crs.log"),
                            fileSizeLimitBytes: settings.LogFileSizeMb * 1024L * 1024,
                            rollOnFileSizeLimit: true, retainedFileCountLimit: settings.LogRetentionFiles + 1)
                        .CreateLogger()
                    : null;
                current?.Dispose();
                current = next;
                Level.MinimumLevel = settings.LoggingEnabled ? minimum : Off;
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (gate) current?.Write(logEvent);
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                Level.MinimumLevel = Off;
                current?.Dispose();
                current = null;
            }
        }
    }

    private sealed class DesktopTextFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            var level = logEvent.Level switch
            {
                LogEventLevel.Information => "Info",
                LogEventLevel.Warning => "Warn",
                _ => logEvent.Level.ToString()
            };
            var time = BeijingClock.Normalize(logEvent.Timestamp).ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            output.WriteLine($"{time}|{level}|{logEvent.RenderMessage(CultureInfo.InvariantCulture)}");
        }
    }
}
