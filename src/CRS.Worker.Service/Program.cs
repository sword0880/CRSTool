using System.Security.Cryptography;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

// 安装器以应用健康确认启动完成；探测模式只读管道，不占用数据目录租约。
if (args is ["--health-probe"])
{
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var response = await new BackgroundPipeClient().QueryAsync("GetHealth", deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(response)); Environment.ExitCode = response.Code == "Ok" && response.Health?.State == "Running" ? 0 : 3;
    }
    catch (Exception error) { Console.Error.WriteLine("CRS_WORKER_PROBE_FAILED:" + error.GetType().Name); Environment.ExitCode = 3; }
    return;
}

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
var trust = Path.Combine(AppContext.BaseDirectory, "trusted-background-plugins.json");
if (console)
{
    data = Path.GetFullPath(Option("--data", data));
    plugins = Path.GetFullPath(Option("--plugins", plugins));
    pipe = Option("--pipe", pipe);
    // 仅隔离控制台允许选择测试信任索引，正式 SCM 模式始终使用程序目录发行索引。
    trust = Path.GetFullPath(Option("--trust", trust));
}
else if (args.Length != 0) throw new ArgumentException("服务安装模式不接受自定义路径或身份参数。");
StorageSecurity.Prepare(data, console);
// 文件独占租约与实际数据库目录绑定，SCM 别名或手动控制台启动也不能形成双写。
using var ownership = new FileStream(Path.Combine(data, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "CRSBackgroundService");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(new ServiceLocations(data, plugins, pipe, trust, console));
builder.Services.AddHostedService<ServiceWorker>();
try { await builder.Build().RunAsync(); }
catch (Exception error)
{
    // 仅记录分类，非零退出使 SCM 恢复策略生效；不输出密钥或第三方异常消息。
    Console.Error.WriteLine("CRS_BACKGROUND_FATAL:" + error.GetType().Name); Environment.ExitCode = 1;
}

namespace CRS.Worker.Service
{
    public sealed record ServiceLocations(string Data, string Plugins, string Pipe, string Trust, bool Console);
    /// <summary>托管服务只组合无界面组件；数据库和插件在 Quartz 停止后释放。</summary>
    public sealed class ServiceWorker(ServiceLocations locations, ILogger<ServiceWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            byte[]? key = null;
            try
            {
                // 在Generic Host已接入SCM后核验较大的快照，仍先于密钥、迁移及插件打开。
                // 未解决状态非零停止；候选先持久标记风险，不能凭未观察到写入自动回退。
                new WorkerUpgradeJournal(locations.Data).BeforeStorageOpen(AppContext.BaseDirectory);
                key = ServiceKey.Open(locations.Data);
                StorageSecurity.Validate(locations.Data, locations.Console); // 新建密钥也须满足数据树权限策略。
                using var repository = new EncryptedTaskRepository(Path.Combine(locations.Data, "scheduler-config.db"), key);
                // 公共通知组件在组合根装配，宿主不包含SMTP实现或业务风险规则。
                var email=new CRS.Notifications.EmailNotificationService(locations.Data);
                var dispatcher=new CRS.Notifications.NotificationDispatcher([new CRS.Notifications.EmailNotificationChannel(email)]);
                var notifications=new WorkerNotificationGateway(email,dispatcher);
                await using var catalog = new BackgroundPluginCatalog(onUnresponsive: () => Environment.Exit(2),
                    hostContext:id=>new WorkerPluginContext(id,locations.Data,key,notifications));
                await catalog.LoadAsync(locations.Plugins, locations.Trust, stoppingToken);
                var diagnostics = new ServiceEventLog(locations.Data);
                var runtime = new BackgroundRuntime(repository, catalog, diagnostics.Write, lifetime.StopApplication,
                    executionSettings: WorkerExecutionSettings.Read(locations.Data),notifications:notifications);
                Task? pipeTask = null;
                using var listeners = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await runtime.StartAsync(stoppingToken);
                    new WorkerUpgradeJournal(locations.Data).MarkHealthy(AppContext.BaseDirectory);
                    diagnostics.Write("Started", "service", "Running");
                    pipeTask = new BackgroundPipeServer(runtime, locations.Pipe).RunAsync(listeners.Token);
                    logger.LogInformation("CRS 后台服务已启动，加载 {Count} 个后台任务。", catalog.Jobs.Count);
                    using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
                    while (await interval.WaitForNextTickAsync(stoppingToken))
                    {
                        if (pipeTask.IsCompleted) await pipeTask; // 管道监听异常必须进入 SCM 失败恢复，不能假装健康。
                        StorageSecurity.Validate(locations.Data, locations.Console); // 发现运行中 ACL 漂移就进入停止清理，不继续调度。
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
