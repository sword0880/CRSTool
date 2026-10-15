using System.Security.Cryptography;
using CRS.Background.Ipc;
using CRS.Background.PluginHost;
using CRS.Background.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 安装模式固定 ProgramData 和管道；控制台隔离参数只能显式启用，避免 SCM 错配两个服务共享库。
var console = args.Contains("--console", StringComparer.Ordinal);
string Option(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CRS", "Background");
var plugins = Path.Combine(AppContext.BaseDirectory, "Plugins");
var pipe = BackgroundPipeServer.DefaultPipeName;
if (console)
{
    data = Path.GetFullPath(Option("--data", data));
    plugins = Path.GetFullPath(Option("--plugins", plugins));
    pipe = Option("--pipe", pipe);
}
else if (args.Length != 0) throw new ArgumentException("服务安装模式不接受自定义路径或身份参数。");
StorageSecurity.Prepare(data, console);
// 文件独占租约与实际数据库目录绑定，SCM 别名或手动控制台启动也不能形成双写。
using var ownership = new FileStream(Path.Combine(data, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "CRSBackgroundService");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(new ServiceLocations(data, plugins, pipe));
builder.Services.AddHostedService<ServiceWorker>();
try { await builder.Build().RunAsync(); }
catch (Exception error)
{
    // 仅记录分类，非零退出使 SCM 恢复策略生效；不输出密钥或第三方异常消息。
    Console.Error.WriteLine("CRS_BACKGROUND_FATAL:" + error.GetType().Name); Environment.ExitCode = 1;
}

namespace CRS.Background.Service
{
    public sealed record ServiceLocations(string Data, string Plugins, string Pipe);
    /// <summary>托管服务只组合无界面组件；数据库和插件在 Quartz 停止后释放。</summary>
    public sealed class ServiceWorker(ServiceLocations locations, ILogger<ServiceWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            byte[]? key = null;
            try
            {
                key = ServiceKey.Open(locations.Data);
                using var repository = new EncryptedTaskRepository(Path.Combine(locations.Data, "scheduler-config.db"), key);
                await using var catalog = new BackgroundPluginCatalog();
                await catalog.LoadAsync(locations.Plugins, Path.Combine(AppContext.BaseDirectory, "trusted-background-plugins.json"), stoppingToken);
                var diagnostics = new ServiceEventLog(locations.Data);
                var runtime = new BackgroundRuntime(repository, catalog, diagnostics.Write);
                Task? pipeTask = null;
                using var listeners = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await runtime.StartAsync(stoppingToken);
                    diagnostics.Write("Started", "service", "Running");
                    pipeTask = new BackgroundPipeServer(runtime, locations.Pipe).RunAsync(listeners.Token);
                    logger.LogInformation("CRS 后台服务已启动，加载 {Count} 个后台任务。", catalog.Jobs.Count);
                    using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
                    while (await interval.WaitForNextTickAsync(stoppingToken))
                    {
                        if (pipeTask.IsCompleted) await pipeTask; // 管道监听异常必须进入 SCM 失败恢复，不能假装健康。
                        await runtime.ReconcileAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                finally
                {
                    listeners.Cancel();
                    diagnostics.Write("Stopping", "service", "Maintenance");
                    try
                    {
                        if (pipeTask is not null) try { await pipeTask; } catch (OperationCanceledException) { }
                    }
                    finally
                    {
                        try { await runtime.DisposeAsync(); }
                        catch (OperationCanceledException)
                        {
                            // 不合作的插件超时后必须终止进程，不能释放仍被使用的数据库后继续运行。
                            logger.LogCritical("后台任务未在停止期限内结束，进程终止以保护存储。");
                            Environment.Exit(2);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogCritical("后台服务失败，类别 {Category}。", error.GetType().Name);
                Environment.ExitCode = 1; lifetime.StopApplication();
            }
            finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
        }
    }
}
