using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using CRS.Worker.Service;

/// <summary>使用真实NTFS文件测量占空比、检查全树覆盖、运行中权限漂移及休息期间的取消。</summary>
internal static class StorageScanBudgetChecks
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++; Console.WriteLine("PASS 巡检预算：" + name);
    }

    public static async Task RunAsync(string root)
    {
        StorageSecurity.Prepare(root, true);
        for (var group = 0; group < 10; group++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "part-" + group));
            for (var file = 0; file < 100; file++) using (File.Create(Path.Combine(directory.FullName, file + ".raw"))) { }
        }
        var process = Process.GetCurrentProcess();
        var baselineCpu = process.TotalProcessorTime;
        var baselineClock = Stopwatch.StartNew();
        var expected = StorageSecurity.Scan(root, true);
        var baselineElapsed = baselineClock.Elapsed;
        baselineCpu = process.TotalProcessorTime - baselineCpu;
        Check(expected == 1010, "全量巡检实际覆盖1000个文件与10个目录");

        var pacedCpu = process.TotalProcessorTime;
        var result = await StorageSecurity.ScanPacedAsync(root, true, default);
        pacedCpu = process.TotalProcessorTime - pacedCpu;
        Check(result.Entries == expected, "默认分批巡检覆盖与全量检查相同的目录项");
        Check(result.Paused > result.Active * 7 && result.Elapsed >= result.Active + result.Paused,
            "默认巡检主动休息且工作时间占比低于约13%");
        var baselineLoad = baselineCpu.TotalSeconds / baselineElapsed.TotalSeconds;
        var pacedLoad = pacedCpu.TotalSeconds / result.Elapsed.TotalSeconds;
        Console.WriteLine(FormattableString.Invariant(
            $"PROFILE 巡检预算：Files={result.Entries};BaselineElapsedMs={baselineElapsed.TotalMilliseconds:F0};BaselineCpuMs={baselineCpu.TotalMilliseconds:F0};PacedElapsedMs={result.Elapsed.TotalMilliseconds:F0};PacedCpuMs={pacedCpu.TotalMilliseconds:F0};ActiveMs={result.Active.TotalMilliseconds:F0};PausedMs={result.Paused.TotalMilliseconds:F0};BaselineLogicalCpu={baselineLoad:F3};PacedLogicalCpu={pacedLoad:F3};LogicalProcessors={Environment.ProcessorCount}"));

        var fullSpeed = StorageScanBudget.Default with { DutyCycle = 1 };
        var corrupt = new FileInfo(Path.Combine(root, "part-0", "0.raw"));
        var original = corrupt.GetAccessControl();
        try
        {
            var widened = corrupt.GetAccessControl();
            widened.SetAccessRuleProtection(true, true);
            widened.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            corrupt.SetAccessControl(widened);
            try { await StorageSecurity.ScanPacedAsync(root, true, default, fullSpeed); throw new Exception("分批巡检放过嵌套文件越权"); }
            catch (StorageSecurityException) { Check(true, "分批巡检仍拒绝受保护嵌套文件中的额外读取权限"); }
        }
        finally { RestoreAccess(corrupt, original); }

        await CompletionDelayAsync(root, fullSpeed);
        await CancellationAsync(root);
        await CriticalDriftAsync(root);
        await CriticalDuringRestAsync(root, fullSpeed);
        Console.WriteLine($"Worker 巡检预算专项通过：{passed} 项。");
    }

    private static async Task CompletionDelayAsync(string root, StorageScanBudget budget)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = Stopwatch.StartNew();
        var finished = new List<(TimeSpan At, StorageScanResult Scan)>();
        var interval = TimeSpan.FromMilliseconds(150);
        var monitor = StorageSecurity.MonitorAsync(root, true, _ => { }, cancel.Token, interval, budget, result =>
        {
            finished.Add((clock.Elapsed, result));
            if (finished.Count == 2) cancel.Cancel();
        });
        try { await monitor; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check(finished.Count == 2, "监控完成两次实际巡检后可以取消");
        var gap = finished[1].At - finished[1].Scan.Elapsed - finished[0].At;
        Check(gap >= interval * .9, "耗时巡检完成后仍等待完整间隔，不立即追赶积压周期");
    }

    private static async Task CancellationAsync(string root)
    {
        using var cancel = new CancellationTokenSource();
        var scan = Task.Run(() => StorageSecurity.ScanPacedAsync(root, true, cancel.Token,
            StorageScanBudget.Default with { MaximumEntriesPerSlice = 1, DutyCycle = .01 }));
        await Task.Delay(150);
        Check(!scan.IsCompleted, "取消测试确实发生在尚未完成的低预算巡检中");
        var clock = Stopwatch.StartNew(); cancel.Cancel();
        try { await scan.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("分批巡检未响应取消"); }
        catch (OperationCanceledException) { Check(clock.Elapsed < TimeSpan.FromSeconds(2), "分批工作与异步休息均及时响应取消"); }
    }

    private static async Task CriticalDriftAsync(string root)
    {
        var key = new FileInfo(Path.Combine(root, "service-key.dpapi"));
        File.WriteAllText(key.FullName, "仅测试ACL，不包含密钥");
        var original = key.GetAccessControl();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scan = Task.Run(() => StorageSecurity.ScanPacedAsync(root, true, cancel.Token,
            StorageScanBudget.Default with { MaximumEntriesPerSlice = 1, DutyCycle = .01, CriticalCheckInterval = TimeSpan.FromMilliseconds(50) }));
        try
        {
            await Task.Delay(200);
            Check(!scan.IsCompleted, "关键文件漂移测试发生在大目录巡检仍在进行时");
            var widened = key.GetAccessControl();
            widened.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            key.SetAccessControl(widened);
            try { await scan.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("运行中未发现密钥文件权限漂移"); }
            catch (StorageSecurityException) { Check(true, "关键文件按独立短周期复核，无需等待全树扫描完成"); }
        }
        finally
        {
            cancel.Cancel();
            try { await scan; } catch (OperationCanceledException) { } catch (StorageSecurityException) { }
            RestoreAccess(key, original);
        }
    }

    private static void RestoreAccess(FileInfo file, FileSecurity original)
    {
        // GetAccessControl返回的快照没有脏标记；显式导入DACL后才会真正恢复到磁盘。
        original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        file.SetAccessControl(original);
    }

    private static async Task CriticalDuringRestAsync(string root, StorageScanBudget budget)
    {
        var key = new FileInfo(Path.Combine(root, "service-key.dpapi"));
        var original = key.GetAccessControl();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = StorageSecurity.MonitorAsync(root, true, _ => { }, cancel.Token, TimeSpan.FromSeconds(1),
            budget with { CriticalCheckInterval = TimeSpan.FromMilliseconds(50) }, _ => completed.TrySetResult());
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var widened = key.GetAccessControl();
            widened.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            key.SetAccessControl(widened);
            try { await monitor.WaitAsync(TimeSpan.FromMilliseconds(800)); throw new Exception("轮间休息时未拒绝关键文件越权"); }
            catch (StorageSecurityException) { Check(true, "轮间休息仍独立检查关键权限，故障会取消并排空另一巡检"); }
        }
        finally
        {
            cancel.Cancel();
            try { await monitor; } catch (OperationCanceledException) { } catch (StorageSecurityException) { }
            RestoreAccess(key, original);
        }
    }
}
