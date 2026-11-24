using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

internal static class VpnChecks
{
    internal static Task RunAsync() => WithScratchAsync(async (root, key) =>
    {
        // 同一份 IPC 节点数据供 VPN 设置及 AI 模型设置共用。
        using (var response = System.Text.Json.JsonDocument.Parse("""{"Node A":120,"Node B":0,"Node C":65535,"Unknown":20}"""))
        {
            var delays = WorkerVpn.ParseBatchDelays(response.RootElement, ["Node A", "Node B", "Node C"]);
            Check(delays.Count == 1 && delays["Node A"] == 120, "批量测速过滤失败值及未知节点");
        }
        var status = new VpnSettingsStatus(true, true, ["Node A", "Node B"]) { Delays = [120, null] };
        Check(status.NodeLabel("Node A") == "Node A（120 ms）" && status.NodeLabel("Node B") == "Node B（未测得）",
            "两个设置入口共用延迟展示规则");
        Check(new VpnSettingsStatus(true, true, ["Node A"]).NodeLabel("Node A") == "Node A（未测速）",
            "兼容旧版 Worker 无延迟字段");
        const string address = "https://example.org/subscription/not-a-real-token";
        using var vpn = new WorkerVpn(root, Path.Combine(root, "not-installed.exe"), key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        Check(!(await vpn.SettingsAsync("owner", true, null, false, token)).Configured, "VPN defaults to unconfigured");
        var saved = await vpn.SettingsAsync("owner", true, address, false, token);
        Check(saved.Configured && !saved.EngineAvailable && saved.Nodes.Length == 0, "Subscription save is independent of node refresh");
        var raw = File.ReadAllText(Path.Combine(root, "worker-vpn.json"));
        Check(!raw.Contains(address) && !raw.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(address))),
            "Provider URL is encrypted at rest");
        Check((await vpn.SettingsAsync("owner", false, null, false, token)).Configured &&
            !(await vpn.SettingsAsync("other", false, null, false, token)).Configured, "Subscription belongs to the Windows owner");
        Check((await vpn.SettingsAsync("owner", false, null, false, token, showAddress: true)).SubscriptionAddress == address &&
            (await vpn.SettingsAsync("owner", false, null, false, token)).SubscriptionAddress is null &&
            (await vpn.SettingsAsync("other", false, null, false, token, showAddress: true)).SubscriptionAddress is null,
            "Only authorized settings reads return the saved subscription address");
        await Expected("Forbidden", () => vpn.OpenAsync("Node A", "other", false, token));
        await Expected("VpnEngineMissing", () => vpn.OpenAsync("Node A", "owner", false, token));
        await Expected("InvalidVpnSubscription", () => vpn.SettingsAsync("owner", true, "http://example.org/path", false, token));
        Check((await vpn.SettingsAsync("owner", false, null, false, token)).Configured, "Invalid changes preserve the saved subscription");

        var file = Path.Combine(root, "scheduler.db");
        using (var repository = new EncryptedTaskRepository(file, key))
        {
            var descriptor = new BackgroundJobDescriptor("first", "First", "0 0 1 * * ?", "China Standard Time", true);
            repository.RegisterDefault("fixture", descriptor);
            repository.RegisterDefault("fixture", descriptor with { JobId = "second" });
            foreach (var plan in repository.Plans())
                repository.Save(plan with { VpnNode = plan.JobId == "first" ? "Node A" : "Node B" }, plan.Revision);
        }
        using (var repository = new EncryptedTaskRepository(file, key))
        {
            var plans = repository.Plans();
            Check(plans.Single(p => p.JobId == "first").VpnNode == "Node A" &&
                plans.Single(p => p.JobId == "second").VpnNode == "Node B", "Jobs retain different nodes across restart");
            var first = plans.Single(p => p.JobId == "first");
            repository.Save(first with { VpnNode = "" }, first.Revision);
            Check(repository.Plans().Single(p => p.JobId == "first").VpnNode == "", "Explicit direct routing is persisted");
        }
        var cleared = await vpn.SettingsAsync("owner", true, "", false, token);
        Check(!cleared.Configured && !File.Exists(Path.Combine(root, "worker-vpn.json")), "Administrator can clear the subscription");
    });

    internal static Task VerifySubscriptionAsync(string engine) => WithScratchAsync(async (root, key) =>
    {
        var address = Console.ReadLine() ?? throw new InvalidOperationException("Missing VPN subscription on stdin.");
        using var vpn = new WorkerVpn(root, Path.GetFullPath(engine), key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = timeout.Token;
        await vpn.SettingsAsync("owner", true, address, false, token);
        var loaded = await vpn.SettingsAsync("owner", false, null, true, token);
        Check(loaded.Nodes.Length > 0, $"Mihomo loaded {loaded.Nodes.Length} provider nodes");
        var first = await vpn.OpenAsync(loaded.Nodes[0], "owner", false, token);
        Check((await vpn.OpenAsync(loaded.Nodes[0], "owner", false, token)).Port == first.Port,
            "Repeated calls reuse the selected node");
        if (loaded.Nodes.Length > 1)
        {
            var second = await vpn.OpenAsync(loaded.Nodes[1], "owner", false, token);
            Check(first.Port != second.Port, "Two selected nodes have independent proxy endpoints");
        }
        await Expected("VpnNodeUnavailable", () => vpn.OpenAsync("CRS test missing node 456563", "owner", false, token));
    });

    private static async Task WithScratchAsync(Func<string, byte[], Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.VpnChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = RandomNumberGenerator.GetBytes(32);
        try { await test(root, key); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }

    private static async Task Expected(string code, Func<Task> action)
    {
        try { await action(); }
        catch (WorkerVpn.VpnException error) when (error.Code == code) { return; }
        throw new InvalidOperationException($"Expected {code}");
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
