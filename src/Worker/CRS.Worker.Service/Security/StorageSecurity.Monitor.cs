using System.Diagnostics;

namespace CRS.Worker.Service;

public static partial class StorageSecurity
{
    /// <summary>完成后再等待下一轮；不会消费积压的定时信号而连续扫描大目录。</summary>
    public static Task MonitorAsync(string directory, bool console, Action<long> capacityWarning,
        CancellationToken token, TimeSpan? frequency = null) =>
        MonitorAsync(directory, console, capacityWarning, token, frequency, StorageScanBudget.Default);

    internal static async Task MonitorAsync(string directory, bool console, Action<long> capacityWarning,
        CancellationToken token, TimeSpan? frequency, StorageScanBudget budget, Action<StorageScanResult>? completed = null)
    {
        budget.Validate();
        var interval = frequency ?? TimeSpan.FromSeconds(30);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frequency));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var full = MonitorFullTreeAsync(directory, console, capacityWarning, stop.Token, interval, budget, completed);
        var critical = MonitorCriticalAsync(directory, console, stop.Token, budget.CriticalCheckInterval);
        try
        {
            var exited = await Task.WhenAny(full, critical).ConfigureAwait(false);
            await exited.ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(full, critical).ConfigureAwait(false);
        }
    }

    private static async Task MonitorCriticalAsync(string directory, bool console, CancellationToken token, TimeSpan interval)
    {
        // 与全树巡检及轮间休息独立，关键权限检查不会因为历史目录很大而被推迟。
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            await Task.Run(() =>
            {
                using var scan = new StorageScan(directory, console, token);
                scan.ValidateTopLevel(token);
            }, token).ConfigureAwait(false);
    }

    private static async Task MonitorFullTreeAsync(string directory, bool console, Action<long> capacityWarning,
        CancellationToken token, TimeSpan interval, StorageScanBudget budget, Action<StorageScanResult>? completed)
    {
        var warned = false;
        while (true)
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            var result = await Task.Run(() => ScanPacedAsync(directory, console, token, budget), token).ConfigureAwait(false);
            if (result.Entries >= CapacityWarningEntries && !warned) { capacityWarning(result.Entries); warned = true; }
            if (result.Entries < CapacityWarningEntries) warned = false;
            completed?.Invoke(result);
        }
    }

    /// <summary>限制单轮连续工作量及工作占空比，根目录和顶层关键文件另按短周期复核。</summary>
    internal static async Task<StorageScanResult> ScanPacedAsync(string directory, bool console,
        CancellationToken token, StorageScanBudget? budget = null)
    {
        budget = (budget ?? StorageScanBudget.Default).Validate();
        var elapsed = Stopwatch.StartNew();
        var active = TimeSpan.Zero;
        var paused = TimeSpan.Zero;
        var nextCriticalCheck = TimeSpan.Zero;
        using var scan = new StorageScan(directory, console, token);
        while (true)
        {
            var slice = Stopwatch.StartNew();
            if (elapsed.Elapsed >= nextCriticalCheck)
            {
                scan.ValidateTopLevel(token);
                nextCriticalCheck = elapsed.Elapsed + budget.CriticalCheckInterval;
            }
            var finished = false;
            for (var entry = 0; entry < budget.MaximumEntriesPerSlice; entry++)
            {
                if (!scan.MoveNext(token)) { finished = true; break; }
                if (slice.Elapsed >= budget.MaximumWorkSlice) break;
            }
            var worked = slice.Elapsed;
            active += worked;
            if (finished) return new(scan.Visited, elapsed.Elapsed, active, paused);
            // 按实际工作时长计算休息；单个慢系统调用超出时间片也不会省掉对应休息。
            var rest = TimeSpan.FromTicks((long)(worked.Ticks * (1 / budget.DutyCycle - 1)));
            if (rest > TimeSpan.Zero)
            {
                var pause = Stopwatch.StartNew();
                await Task.Delay(rest, token).ConfigureAwait(false);
                paused += pause.Elapsed;
            }
        }
    }
}

internal sealed record StorageScanResult(long Entries, TimeSpan Elapsed, TimeSpan Active, TimeSpan Paused);

/// <summary>默认最多连续工作20ms/128项，占空比10%；不按整机核数扩大巡检预算。</summary>
internal sealed record StorageScanBudget(TimeSpan MaximumWorkSlice, int MaximumEntriesPerSlice,
    double DutyCycle, TimeSpan CriticalCheckInterval)
{
    internal static StorageScanBudget Default { get; } = new(TimeSpan.FromMilliseconds(20), 128, .1, TimeSpan.FromSeconds(30));

    internal StorageScanBudget Validate()
    {
        if (MaximumWorkSlice <= TimeSpan.Zero || MaximumWorkSlice > TimeSpan.FromSeconds(1) ||
            MaximumEntriesPerSlice is < 1 or > 4096 || !double.IsFinite(DutyCycle) || DutyCycle is < .01 or > 1 ||
            CriticalCheckInterval < TimeSpan.FromMilliseconds(10) || CriticalCheckInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(StorageScanBudget));
        return this;
    }
}
