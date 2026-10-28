using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

/// <summary>真实SQLCipher、DPAPI与独立Worker启动验证；不会控制已安装服务。</summary>
internal static class WorkerUpgradeChecks
{
    private static int passed;
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS 升级：" + name); }
    private static void Save(string data, WorkerUpgradeRecord record)
    {
        Directory.CreateDirectory(Path.Combine(data, "maintenance"));
        File.WriteAllBytes(Path.Combine(data, "maintenance", "upgrade.json"), JsonSerializer.SerializeToUtf8Bytes(record));
    }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException)
        { Check(true, name); return; }
        throw new InvalidOperationException("未拒绝：" + name);
    }
    internal static WorkerUpgradeRecord Seed(string data, string program, string pluginId, string jobId)
    {
        StorageSecurity.Prepare(data, true);
        var key = ServiceKey.Open(data);
        try
        {
            using var repository = new EncryptedTaskRepository(Path.Combine(data, "scheduler-config.db"), key);
            repository.RegisterDefault(pluginId, new BackgroundJobDescriptor(jobId, "升级前心跳", "0 0 0 1 1 ?", "China Standard Time", false));
            var plan = repository.Plans().Single();
            var execution = repository.Begin(plan, DateTimeOffset.UtcNow);
            repository.End(execution, plan, BackgroundJobResult.Success("before-upgrade"));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        Directory.CreateDirectory(Path.Combine(data, "plugins", "crs.synthetic", "raw"));
        File.WriteAllText(Path.Combine(data, "plugins", "crs.synthetic", "raw", "evidence.raw"), "synthetic-human-review-evidence");
        var record = new WorkerUpgradeRecord(1, Guid.NewGuid().ToString("N"), new string('A', 64),
            WorkerUpgradeJournal.ReleaseHash(program), "2.0.0", "2.0.0", "Prepared", false, DateTimeOffset.UtcNow);
        var files = Directory.GetFiles(data, "*", SearchOption.AllDirectories);
        var snapshotRoot = Path.Combine(data, "maintenance", record.Id);
        var snapshot = Path.Combine(snapshotRoot, "snapshot");
        var hashes = new Dictionary<string, string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(data, file).Replace('\\', '/');
            var target = Path.Combine(snapshot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
            hashes[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        var manifest = Path.Combine(snapshotRoot, "snapshot.json");
        File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(new { Format = 1, ReleaseHash = record.PreviousReleaseHash, Files = hashes }));
        record = record with { BackupManifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))) };
        Save(data, record); return record;
    }
    public static async Task RunAsync(string root, string program, string pluginId = "crs.heartbeat", string jobId = "heartbeat")
    {
        var data = Path.Combine(root, "upgrade-state");
        var record = Seed(data, program, pluginId, jobId); var journal = new WorkerUpgradeJournal(data);
        var database = Path.Combine(data, "scheduler-config.db"); var keyPath = Path.Combine(data, "service-key.dpapi");
        var dbHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)));
        var keyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keyPath)));
        foreach (var stage in new[] { "Prepared", "BackedUp", "Staged", "RollingBack", "NeedsManualRepair", "Starting", "Healthy" })
        {
            Save(data, record with { Stage = stage, NewFactsPossible = stage is "Starting" or "Healthy" });
            Reject(() => journal.BeforeStorageOpen(program), stage + "重启拒绝自动打开数据");
        }
        Check(dbHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database)))
            && keyHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(keyPath))), "所有未解决阶段均保留原数据库和机器密钥");
        Save(data, record with { Stage = "Switched", CandidateReleaseHash = new string('B', 64) });
        Reject(() => journal.BeforeStorageOpen(program), "错误发行指纹拒绝打开数据");
        var evidence = Path.Combine(data, "maintenance", record.Id, "snapshot", "plugins", "crs.synthetic", "raw", "evidence.raw");
        var original = File.ReadAllBytes(evidence); File.AppendAllText(evidence, "tamper");
        Save(data, record with { Stage = "Switched" });
        Reject(() => journal.BeforeStorageOpen(program), "备份原件被篡改时拒绝候选启动");
        File.WriteAllBytes(evidence, original);
        journal.BeforeStorageOpen(program);
        Check(journal.Read() is { Stage: "Starting", NewFactsPossible: true }, "打开密钥和数据库之前先持久标记新事实风险");
        Reject(() => journal.BeforeStorageOpen(program), "首次打开后强杀不能靠SCM重启清除风险");
        Reject(()=>journal.MarkHealthy(program,[new(pluginId,"Failed","SyntheticDatabaseFormat")],[pluginId]),"插件加载失败时不能把宿主运行当作维护健康");
        Reject(()=>journal.MarkHealthy(program,[],[pluginId]),"遗漏应加载插件不能确认维护健康");
        Reject(()=>journal.MarkHealthy(program,[new(pluginId,"Started"),new(pluginId,"Started")],[pluginId]),"重复插件状态不能充当完整启动证据");
        Check(journal.Read()!.Stage=="Starting","失败的健康确认保留本次风险阶段，不误写Healthy");
        journal.MarkHealthy(program,[new(pluginId,"Started")],[pluginId]);
        Check(journal.Read() is { Stage: "Healthy", NewFactsPossible: true }, "应用就绪不自动提交维护事务");
        Save(data, journal.Read()! with { Stage = "Committed" });
        journal.BeforeStorageOpen(program);
        Check(journal.Read()!.Stage == "Committed", "显式确认的同发行正常重启");
        Save(data, record with { Stage = "RolledBack", NewFactsPossible = true });
        Reject(() => journal.Read(), "日志不能把有新事实的代次标成已回退");
        var previous = Path.Combine(root, "previous-release"); Directory.CreateDirectory(previous);
        File.Copy(Path.Combine(program, "release.json"), Path.Combine(previous, "release.json"));
        Save(data, record with { Stage = "RolledBack", PreviousReleaseHash = WorkerUpgradeJournal.ReleaseHash(previous) });
        journal.BeforeStorageOpen(previous);
        Check(journal.Read()!.Stage == "RolledBack", "无新事实且原发行相符才允许回退启动");
        await ProcessStagesAsync(root, program, pluginId, jobId);
        await PluginFailureAsync(root,program,pluginId,jobId);
        File.WriteAllText(Path.Combine(root,"upgrade-checks-acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticWorkerUpgradeStartupAndPluginHealth",Root=root},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Worker升级启动专项通过：{passed}项。");
        Console.WriteLine("隔离证据："+Path.Combine(root,"upgrade-checks-acceptance.json"));
    }
    private static Process Launch(string program, string data, string pipe)
    {
        var info = new ProcessStartInfo(Path.Combine(program, "CRS.Worker.Service.exe"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "--console", "--data", data, "--plugins", Path.Combine(program, "Plugins"), "--pipe", pipe }) info.ArgumentList.Add(argument);
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
    }
    private static async Task PluginFailureAsync(string root,string program,string pluginId,string jobId)
    {
        // 只改变测试自有发行副本的入口和信任摘要，不修改候选包或已安装服务。
        var failedProgram=Path.Combine(root,"plugin-failure-program");Directory.CreateDirectory(failedProgram);
        foreach(var file in Directory.GetFiles(program,"*",SearchOption.AllDirectories))
        {var target=Path.Combine(failedProgram,Path.GetRelativePath(program,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target);}
        var indexPath=Path.Combine(failedProgram,"trusted-background-plugins.json");
        var packages=JsonSerializer.Deserialize<CRS.Worker.PluginHost.TrustedPackage[]>(File.ReadAllBytes(indexPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        var package=packages.Single(x=>x.Id==pluginId);var manifest=Path.Combine(failedProgram,"Plugins",package.Directory,"plugin.json");
        var json=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllBytes(manifest))!;
        json["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.FixturePlugin.dll",type="CRS.Worker.FixturePlugin.BadStartEntry"});File.WriteAllBytes(manifest,JsonSerializer.SerializeToUtf8Bytes(json));
        package.Files["plugin.json"]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));File.WriteAllBytes(indexPath,JsonSerializer.SerializeToUtf8Bytes(packages));
        var data=Path.Combine(root,"plugin-failure-data");var record=Seed(data,failedProgram,pluginId,jobId);Save(data,record with{Stage="Switched"});
        using(var failed=Launch(failedProgram,data,"CRS.Upgrade.BadPlugin."+Guid.NewGuid().ToString("N")))
        {
            try
            {
                await failed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Check(failed.ExitCode!=0&&new WorkerUpgradeJournal(data).Read() is{Stage:"Starting",NewFactsPossible:true},"真实维护候选插件启动失败时非零退出，不能误写Healthy");
            }
            finally{if(!failed.HasExited){failed.Kill(entireProcessTree:true);await failed.WaitForExitAsync();}}
        }
        var normalData=Path.Combine(root,"ordinary-plugin-failure");var pipe="CRS.Ordinary.BadPlugin."+Guid.NewGuid().ToString("N");using var ordinary=Launch(failedProgram,normalData,pipe);
        try
        {
            var client=new BackgroundPipeClient(pipe);var response=await WorkerProcessReadiness.WaitAsync(client,ordinary);
            var jobs=await client.QueryAsync("GetJobs");
            Check(response.Health?.State=="Degraded"&&response.Plugins?.SingleOrDefault(x=>x.PluginId==pluginId)?.State=="Failed"
                &&jobs.Code=="Ok"&&jobs.Jobs?.Count==0,"普通启动保留Degraded及失败插件诊断，查询通道可用且不登记故障插件任务");
        }
        finally{if(!ordinary.HasExited){ordinary.Kill(entireProcessTree:true);await ordinary.WaitForExitAsync();}}
    }
    private static async Task ProcessStagesAsync(string root, string program, string pluginId, string jobId)
    {
        var data = Path.Combine(root, "upgrade-process"); var record = Seed(data, program, pluginId, jobId);
        var pipe = "CRS.Worker.Upgrade." + Guid.NewGuid().ToString("N");
        using (var blocked = Launch(program, data, pipe))
        {
            try { await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Check(blocked.ExitCode != 0, "独立服务进程在Prepared阶段拒绝启动"); }
            finally { if (!blocked.HasExited) { blocked.Kill(); await blocked.WaitForExitAsync(); } }
        }
        Save(data, record with { Stage = "Switched" });
        var client = new BackgroundPipeClient(pipe);
        using (var candidate = Launch(program, data, pipe))
        {
            try
            {
                var health = await WorkerProcessReadiness.WaitAsync(client,candidate);
                var journal = new WorkerUpgradeJournal(data).Read();
                var diagnostics = $"code={health.Code}; health={health.Health?.State ?? "<null>"}; journal={journal?.Stage ?? "<null>"}; newFacts={journal?.NewFactsPossible}; exited={candidate.HasExited}; exitCode={(candidate.HasExited ? candidate.ExitCode : 0)}";
                Check(health.Health?.State == "Running" && journal is { Stage: "Healthy", NewFactsPossible: true },
                    "真实Worker健康端点出现前风险与就绪阶段已持久化 (" + diagnostics + ")");
                var history = await client.QueryAsync("GetHistory");
                Check(history.History!.Any(x => x.Checkpoint == "before-upgrade"), "候选启动保留旧执行事实");
            }
            finally { if (!candidate.HasExited) { candidate.Kill(); await candidate.WaitForExitAsync(); } }
        }
        using (var interrupted = Launch(program, data, pipe))
        {
            try { await interrupted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Check(interrupted.ExitCode != 0, "健康后尚未Commit的真实强杀重启进入维护"); }
            finally { if (!interrupted.HasExited) { interrupted.Kill(); await interrupted.WaitForExitAsync(); } }
        }
        Save(data, new WorkerUpgradeJournal(data).Read()! with { Stage = "Committed" });
        using var confirmed = Launch(program, data, pipe);
        try { Check((await WorkerProcessReadiness.WaitAsync(client,confirmed)).Health?.State == "Running", "确认候选后真实进程恢复正常启动"); }
        finally { if (!confirmed.HasExited) { confirmed.Kill(); await confirmed.WaitForExitAsync(); } }
    }
}
