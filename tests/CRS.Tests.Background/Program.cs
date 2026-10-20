using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;
if(args is ["--task-storage-boundary"])
{
    var boundaryRoot=Path.Combine(Path.GetTempPath(),"CRS.TaskStorageBoundary",Guid.NewGuid().ToString("N"));
    await TaskRepositoryBoundaryChecks.RunAsync(boundaryRoot);
    await WorkerRemediationChecks.StorageFaultsOnlyAsync(boundaryRoot);return;
}
if(args is ["--source-budget-process",var sourcePackage]){await SourceBudgetProcessChecks.RunAsync(sourcePackage);return;}
if(args is ["--notifications"]){await NotificationChecks.Run();await WebhookNotificationChecks.Run();return;}
if(args is ["--ipc-boundaries"]){await PipeBoundaryChecks.RunAsync();return;}
if(args is ["--lifecycle-only"]){await LifecycleBoundaryChecks.Run(Path.Combine(Path.GetTempPath(),"CRS.Lifecycle",Guid.NewGuid().ToString("N")));return;}
if(args is ["--lifecycle-exit-child",var lifecycleRoot,var lifecyclePhase]){await LifecycleBoundaryChecks.ExitChild(lifecycleRoot,lifecyclePhase);return;}

// 验收工具只操作自己创建的隔离目录和进程；--probe 可只读检查实际安装的服务。
if(args is ["--isolated-fact-create",var factPipe])
{
    var client=new BackgroundPipeClient(factPipe);
    var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.heartbeat","heartbeat");
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var accepted=await client.SendAsync(request,deadline.Token);
    if(accepted.Code!="Accepted")throw new InvalidOperationException("隔离事实创建未被接受。");
    while(true)
    {
        var response=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId),deadline.Token);
        if(response.Receipt?.State=="Completed")
        {
            if(response.Receipt.Code!="Succeeded" || response.Receipt.ExecutionId is null)throw new InvalidOperationException("隔离事实未成功提交。");
            Console.WriteLine(response.Receipt.ExecutionId);return;
        }
        await Task.Delay(50,deadline.Token);
    }
}
if(args is ["--isolated-fact-read",var readFactPipe,var factId])
{
    var response=await new BackgroundPipeClient(readFactPipe).SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:factId));
    if(response.Execution?.ExecutionId!=factId || response.Execution.Result!="Succeeded")throw new InvalidOperationException("隔离事实未保留。");
    return;
}
if(args is ["--isolated-health-probe",var isolatedPipe])
{
    try
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var reply=await new BackgroundPipeClient(isolatedPipe).QueryAsync("GetHealth",deadline.Token);
        Environment.ExitCode=reply.Health?.State=="Running"?0:3;
    }
    catch(Exception){Environment.ExitCode=3;}
    return;
}
if(args is ["--upgrade-guard-probe", var guardData, var guardProgram])
{
    new WorkerUpgradeJournal(Path.GetFullPath(guardData)).BeforeStorageOpen(Path.GetFullPath(guardProgram));
    Console.WriteLine("PASS 升级互操作：PowerShell日志与快照通过C#启动门禁。");return;
}
if(args is ["--upgrade-only", var upgradePackage])
{
    var upgradeRoot=Path.Combine(Path.GetTempPath(),"CRS.Worker.Upgrade.Tests",Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(upgradeRoot);
    await WorkerUpgradeChecks.RunAsync(upgradeRoot,Path.GetFullPath(upgradePackage));return;
}
if(args is ["--scheduling-only"])
{
    var schedulingRoot=Path.Combine(Path.GetTempPath(),"CRS.Worker.Scheduling.Tests",Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(schedulingRoot);await WorkerSchedulingChecks.RunAsync(schedulingRoot);return;
}
if(args.Contains("--execution-probe") || args.Contains("--etf-progress-probe"))
{
    // 仅查询既有公开执行，不创建任务、触发采集或打开服务数据库。
    var client=new BackgroundPipeClient();
    var history=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistory",PluginId:args.Contains("--etf-progress-probe")?"crs.national-team":null));var row=history.History?.FirstOrDefault()??throw new InvalidOperationException("尚无执行记录。");
    var detail=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:row.ExecutionId));
    if(detail.Code!="Ok" || detail.Execution?.ExecutionId!=row.ExecutionId)throw new InvalidOperationException("实际服务日志查询失败。");
    Console.WriteLine(JsonSerializer.Serialize(detail));return;
}
if (args.Contains("--readonly-check"))
{
    if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { Environment.ExitCode = 10; return; }
    var client = new BackgroundPipeClient();
    var health = await client.QueryAsync("GetHealth");
    var jobs = await client.QueryAsync("GetJobs");
    var plan = jobs.Jobs!.First();
    var denied = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "Pause", plan.PluginId, plan.JobId, ExpectedRevision: plan.Revision));
    Environment.ExitCode = health.Code == "Ok" && denied.Code == "Forbidden" ? 0 : 11;
    return;
}
if (args.Contains("--acl-probe"))
{
    await RestrictedCallerChecks.RunAsync(); return;
}
// 只读核验当前正式目录，独立于隔离夹具；不会调整已安装服务的权限或密钥。
if (args.Contains("--storage-acl-probe"))
{
    StorageSecurity.Validate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CRS", "Background"), false);
    Console.WriteLine("PASS 当前安装数据树符合正式服务权限策略。"); return;
}
if (args.Contains("--probe"))
{
    var client = new BackgroundPipeClient();
    foreach (var command in new[] { "GetHealth", "GetJobs", "GetHistory" })
        Console.WriteLine(JsonSerializer.Serialize(await client.QueryAsync(command)));
    return;
}
var package = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("需指定 Publish-BackgroundService 生成的发行目录。");
var root = Path.Combine(Path.GetTempPath(), "CRS.Background.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); StorageSecurity.Prepare(root, true);
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action work, string name) { try { work(); } catch (Exception) { Check(true, name); return; } throw new Exception("未拒绝：" + name); }
var key = RandomNumberGenerator.GetBytes(32);
ExecutionProgressChecks.Run(root,key);
var database = Path.Combine(root, "scheduler.db");
var descriptor = new BackgroundJobDescriptor("heartbeat", "心跳", "0 0 0 * * ?", "China Standard Time", true);
using (var repository = new EncryptedTaskRepository(database, key))
{
    repository.RegisterDefault("crs.heartbeat", descriptor);
    var plan = repository.Plans().Single();
    repository.Save(plan with { Enabled = false, Cron = "0 0 1 * * ?" }, plan.Revision);
    Reject(() => repository.Save(plan, plan.Revision), "并发编辑修订号拒绝");
    var id = repository.Begin(plan, DateTimeOffset.UtcNow);
    repository.End(id, plan, BackgroundJobResult.Success("checkpoint-1"));
    Check(repository.History().Single().Result == "Succeeded", "执行结果与检查点事务");
    var failed = repository.Begin(plan, DateTimeOffset.UtcNow);
    repository.End(failed, plan, new("AccessBlocked"));
    Check(repository.Plans().Single().NotBefore > DateTimeOffset.UtcNow.AddHours(23), "权限阻断持久退避");
    repository.Begin(plan, DateTimeOffset.UtcNow); // 模拟已提交 Running 后进程消失。
    var backup = Path.Combine(root, "snapshot.db"); repository.Snapshot(backup);
    using var snapshot = new EncryptedTaskRepository(backup, key);
    Check(snapshot.History().Any(x => x.Result == "Succeeded"), "SQLCipher 在线快照包含 WAL 已提交事实");
}
using (var reopened = new EncryptedTaskRepository(database, key))
{
    Check(reopened.Plans().Single() is { Enabled: false, Revision: 2, Cron: "0 0 1 * * ?" }, "重启恢复唯一权威计划");
    Check(reopened.History().Any(x => x.Result == "Interrupted"), "遗留运行记录标记中断");
}
Check(!System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(database), 0, 16).StartsWith("SQLite format"), "数据库文件头非明文 SQLite");
Reject(() => { using var plain = new SqliteConnection($"Data Source={database};Pooling=False"); plain.Open(); using var command = plain.CreateCommand(); command.CommandText = "SELECT * FROM task"; command.ExecuteScalar(); }, "无密钥拒读数据库");
Reject(() => { using var wrong = new EncryptedTaskRepository(database, RandomNumberGenerator.GetBytes(32)); }, "错误密钥拒读且不覆盖");
var missingKey = Path.Combine(root, "missing-key"); Directory.CreateDirectory(missingKey); File.Copy(database, Path.Combine(missingKey, "scheduler-config.db"));
Reject(() => ServiceKey.Open(missingKey), "已有数据库缺钥拒绝生成新密钥");
var keyFolder = Path.Combine(root, "keys"); Directory.CreateDirectory(keyFolder);
var first = ServiceKey.Open(keyFolder); var second = ServiceKey.Open(keyFolder);
Check(first.SequenceEqual(second), "机器 DPAPI 独立服务密钥可重启解封"); CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second);
var plainPath = Path.Combine(root, "plain.db");
using (var plain = new SqliteConnection($"Data Source={plainPath};Pooling=False")) { plain.Open(); using var command = plain.CreateCommand(); command.CommandText = "CREATE TABLE old_data(x)"; command.ExecuteNonQuery(); }
Reject(() => { using var rejected = new EncryptedTaskRepository(plainPath, key); }, "拒绝将旧明文库当服务库迁移");

await using (var catalog = new BackgroundPluginCatalog())
{
    await catalog.LoadAsync(Path.Combine(package, "Plugins"), Path.Combine(package, "trusted-background-plugins.json"), default);
    Check(catalog.Jobs.Count == 1, "真实受信后台插件发现和启动");
    Check(!AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name is "PresentationFramework" or "PresentationCore" or "CRS.Security" or "CRS.Infrastructure"), "无 WPF 或税务程序集载入");
    using var repository = new EncryptedTaskRepository(Path.Combine(root, "runtime.db"), key);
    await using var runtime = new BackgroundRuntime(repository, catalog); await runtime.StartAsync(default);
    var plan = repository.Plans().Single(); var caller = new BackgroundCaller(WindowsIdentity.GetCurrent().User!.Value, true);
    async Task<BackgroundResponse> Command(string command, long? revision = null, string? cron = null) => await runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(), command, plan.PluginId, plan.JobId, cron, plan.TimeZoneId, revision), caller, default);
    Check((await runtime.HandleAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "RunNow", plan.PluginId, plan.JobId), caller with { CanManage = false }, default)).Code == "Forbidden", "服务控制层再次检查管理权限");
    Check((await Command("Pause", plan.Revision)).Code == "Ok", "暂停持久计划并移除启动触发");
    await Task.Delay(2500);
    Check(repository.History().Count == 0, "暂停后无启动触发残留");
    Check((await Command("RunNow")).Code == "Accepted", "暂停计划仍允许授权手动触发");
    var until = DateTimeOffset.UtcNow.AddSeconds(8);
    while (!repository.History().Any(x => x.Result == "Succeeded") && DateTimeOffset.UtcNow < until) await Task.Delay(50);
    Check(repository.History().Any(x => x.Result == "Succeeded"), "真实 Quartz 分发执行并持久历史");
    Check((await Command("ChangeSchedule", repository.Plans().Single().Revision, "bad-cron")).Code == "InvalidOrStaleRequest", "非法 Cron 在提交前拒绝");
    var pipeName = "CRS.Background.Tests." + Guid.NewGuid().ToString("N");
    using var stopPipe = new CancellationTokenSource(); var listening = new BackgroundPipeServer(runtime, pipeName).RunAsync(stopPipe.Token);
    var client = new BackgroundPipeClient(pipeName);
    Check((await client.QueryAsync("GetHealth")).Health?.State == "Running", "真实 Named Pipes Windows 令牌查询");
    var repeat = new BackgroundRequest(WorkerProtocol.Version, Guid.NewGuid(), "GetJobs");
    Check((await client.SendAsync(repeat)).Code == "Ok" && (await client.SendAsync(repeat)).Code == "Ok", "只读重复查询不占用全局重放额度");
    Check((await client.SendAsync(new(99, Guid.NewGuid(), "RunNow"))).Code == "ProtocolMismatch", "协议不兼容拒绝修改");
    Check((await client.QueryAsync("LoadAssembly")).Code == "UnknownCommand", "管道命令白名单拒绝任意加载");
    stopPipe.Cancel(); try { await listening; } catch (OperationCanceledException) { }
}

// 独立子进程验证服务宿主启动、真实强杀与数据库租约；只终止本测试持有的 PID。
var processData = Path.Combine(root, "process"); var processPipe = "CRS.Background.Process." + Guid.NewGuid().ToString("N");
Process Launch()
{
    var info = new ProcessStartInfo(Path.Combine(package, "CRS.Worker.Service.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var value in new[] { "--console", "--data", processData, "--plugins", Path.Combine(package, "Plugins"), "--pipe", processPipe }) info.ArgumentList.Add(value);
    var process = Process.Start(info)!; process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
}
async Task WaitPipe(BackgroundPipeClient client, Process process)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    if (process.HasExited) throw new Exception("服务进程提前退出：" + process.ExitCode);
    await client.QueryAsync("GetHealth", timeout.Token);
}
using (var child = Launch())
{
    try
    {
        var client = new BackgroundPipeClient(processPipe); await WaitPipe(client, child);
        Check(true, "独立无桌面服务进程启动并提供管理管道");
        using var duplicate = Launch(); await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(duplicate.ExitCode != 0 && !child.HasExited, "同目录第二服务进程拒绝且首实例继续运行");
        var plan = (await client.QueryAsync("GetJobs")).Jobs!.First();
        var response = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "Pause", plan.PluginId, plan.JobId, ExpectedRevision: plan.Revision));
        Check(response.Code == "Ok", "真实管道管理员授权修改计划");
    }
    finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
}
using (var restored = Launch())
{
    try
    {
        var client = new BackgroundPipeClient(processPipe); await WaitPipe(client, restored);
        Check((await client.QueryAsync("GetJobs")).Jobs!.Single() is { Enabled: false, Revision: 2 }, "真实强杀重启保留暂停计划且只重建一个任务");
    }
    finally { if (!restored.HasExited) restored.Kill(); await restored.WaitForExitAsync(); }
}

// 复制后篡改插件，确保不是从插件自己的 JSON 接受其自报哈希。
var tampered = Path.Combine(root, "tampered"); Directory.CreateDirectory(tampered);
foreach (var file in Directory.EnumerateFiles(Path.Combine(package, "Plugins"), "*", SearchOption.AllDirectories))
{
    var destination = Path.Combine(tampered, Path.GetRelativePath(Path.Combine(package, "Plugins"), file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
}
await File.AppendAllTextAsync(Path.Combine(tampered, "crs.heartbeat", "2.0.0", "plugin.json"), " ");
await using (var rejected = new BackgroundPluginCatalog())
{
    await rejected.LoadAsync(tampered, Path.Combine(package, "trusted-background-plugins.json"), default);
    Check(rejected.Jobs.Count == 0 && rejected.Statuses.Single().State == "Failed", "外部信任索引隔离已篡改后台包");
}
await WorkerRemediationChecks.RunAsync(root, package);
await LifecycleBoundaryChecks.Run(root);
await WorkerSchedulingChecks.RunAsync(root);
await WorkerUpgradeChecks.RunAsync(root, package);
await StorageSecurityChecks.RunAsync(root, package);
CryptographicOperations.ZeroMemory(key);
Console.WriteLine($"后台服务验证通过：{passed} 项；隔离现场：{root}");
