using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.IO;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;

namespace CRS.Worker.Service;

internal sealed class WorkerVpn(string dataDirectory, string executable, byte[] key) : IDisposable
{
    private const string Provider = "crs-subscription";
    private const string Group = "crs-selected-node";
    private const string Purpose = "worker-vpn-settings/v1";
    private readonly string settingsPath = Path.Combine(dataDirectory, "worker-vpn.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private string[] nodes = [];
    private Dictionary<string, int>? delayResults;

    internal sealed class VpnException(string code) : Exception(code)
    {
        internal string Code => Message;
    }

    private sealed record Subscription(string Owner, byte[] Address);

    internal async Task<VpnSettingsStatus> SettingsAsync(string sid, bool admin, string? update, bool refresh, CancellationToken token,
        bool showAddress = false)
    {
        var elapsed = Stopwatch.StartNew();
        await gate.WaitAsync(token);
        try
        {
            var current = Read();
            if (!admin && current?.Owner != sid) return new(false, File.Exists(executable), []);
            if (update is not null)
            {
                if (!admin) throw new VpnException("Forbidden");
                if (update.Length == 0)
                {
                    File.Delete(PhysicalPathGuard.RejectLinks(settingsPath));
                    Reset();
                    return new(false, File.Exists(executable), []);
                }
                ValidateAddress(update);
                var bytes = Encoding.UTF8.GetBytes(update);
                try
                {
                    current = new(sid, ProtectedData.Protect(bytes, key, DataProtectionScope.CurrentUser));
                    AuthenticatedJsonFile.Write(settingsPath, key, Purpose, current);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                Reset();
            }
            if (refresh)
            {
                using var process = await StartAsync(current, token);
                nodes = await ReadNodesAsync(process, token);
                delayResults = null;
                // IPC 服务端请求上限为 20 秒。测速失败不丢弃已经成功读取的节点。
                var remaining = TimeSpan.FromSeconds(18) - elapsed.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probe.CancelAfter(remaining);
                    try
                    {
                        using var results = await ReadJsonAsync(process.Controller,
                            "group/" + Group + "/delay?url=https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204&timeout=3000&expected=204",
                            probe.Token);
                        delayResults = ParseBatchDelays(results.RootElement, nodes);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException) { }
                }
            }
            return new(current is not null, File.Exists(executable), nodes)
            {
                SubscriptionAddress = showAddress && current is not null ? ReadAddress(current) : null,
                Delays = delayResults is null ? null : nodes.Select(name =>
                    delayResults.TryGetValue(name, out var delay) ? (int?)delay : null).ToArray()
            };
        }
        finally { gate.Release(); }
    }

    internal async Task<VpnProxyAccess> OpenAsync(string node, string? sid, bool admin, CancellationToken token)
    {
        if (!ValidNode(node)) throw new VpnException("VpnNodeUnavailable");
        await gate.WaitAsync(token);
        try
        {
            var current = Read();
            if (sid is not null && !admin && current?.Owner != sid) throw new VpnException("Forbidden");
            var idleBefore = DateTimeOffset.UtcNow.AddMinutes(-30);
            foreach (var item in connections.Where(item => item.Value.Process.HasExited || item.Value.LastUsed < idleBefore).ToArray())
            {
                connections.Remove(item.Key);
                item.Value.Dispose();
            }
            if (nodes.Length != 0 && !nodes.Contains(node, StringComparer.Ordinal))
                throw new VpnException("VpnNodeUnavailable");
            if (connections.TryGetValue(node, out var active))
            {
                active.LastUsed = DateTimeOffset.UtcNow;
                return active.Proxy;
            }
            if (connections.Count >= 16) throw new VpnException("VpnCapacity");
            var candidate = await StartAsync(current, token);
            try
            {
                // 为每个节点保留独立的代理进程。确认节点切换成功后才向调用方返回本机端口。
                nodes = await ReadNodesAsync(candidate, token);
                if (!nodes.Contains(node, StringComparer.Ordinal)) throw new VpnException("VpnNodeUnavailable");
                using var content = new StringContent(JsonSerializer.Serialize(new { name = node }), Encoding.UTF8, "application/json");
                using var reply = await candidate.Controller.PutAsync("proxies/" + Group, content, token);
                if (!reply.IsSuccessStatusCode) throw new VpnException("VpnNodeUnavailable");
                using var group = await ReadJsonAsync(candidate.Controller, "proxies/" + Group, token);
                if (!group.RootElement.TryGetProperty("now", out var selected) || selected.GetString() != node)
                    throw new VpnException("VpnNodeUnavailable");
                connections.Add(node, candidate);
                return candidate.Proxy;
            }
            catch { candidate.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    internal static bool ValidNode(string node) =>
        node.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(node) && !node.Any(char.IsControl) &&
        node is not ("DIRECT" or "REJECT" or "PASS" or "COMPATIBLE" or Group);

    private static void ValidateAddress(string address)
    {
        if (address.Length is < 10 or > 3000 || address.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.HostNameType != UriHostNameType.Dns || !url.IdnHost.Contains('.') || url.IsLoopback ||
            url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new VpnException("InvalidVpnSubscription");
    }

    private Subscription? Read()
    {
        try { return AuthenticatedJsonFile.Read<Subscription>(settingsPath, key, Purpose); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private async Task<Connection> StartAsync(Subscription? subscription, CancellationToken token)
    {
        if (subscription is null) throw new VpnException("VpnNotConfigured");
        if (!File.Exists(executable)) throw new VpnException("VpnEngineMissing");
        PhysicalPathGuard.RejectLinks(executable);
        var address = ReadAddress(subscription);
        ValidateAddress(address);

        var home = PhysicalPathGuard.RejectLinks(Path.Combine(dataDirectory, "vpn-runtime"));
        Directory.CreateDirectory(home);
        var cache = "crs-provider-" + Guid.NewGuid().ToString("N") + ".yaml";
        var proxyPort = FreePort();
        int controllerPort;
        do { controllerPort = FreePort(); } while (controllerPort == proxyPort);
        var access = new VpnProxyAccess { Port = proxyPort, Password = Secret() };
        var secret = Secret();
        var config = new Dictionary<string, object>
        {
            ["allow-lan"] = false, ["bind-address"] = "127.0.0.1", ["mode"] = "rule", ["log-level"] = "silent",
            ["port"] = proxyPort, ["authentication"] = new[] { "crs:" + access.Password },
            ["external-controller"] = $"127.0.0.1:{controllerPort}", ["secret"] = secret,
            ["skip-auth-prefixes"] = Array.Empty<string>(),
            ["profile"] = new Dictionary<string, bool> { ["store-selected"] = false, ["store-fake-ip"] = false },
            ["tun"] = new { enable = false }, ["geo-auto-update"] = false,
            ["proxy-providers"] = new Dictionary<string, object> { [Provider] = new
                { type = "http", url = address, path = "./" + cache, interval = 0, proxy = "DIRECT",
                  header = new Dictionary<string, string[]> { ["User-Agent"] = ["clash.meta"] } } },
            ["proxy-groups"] = new[] { new { name = Group, type = "select", use = new[] { Provider } } },
            ["rules"] = new[] { "MATCH," + Group }
        };
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("CLASH_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(home);
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("-");
        var process = Process.Start(start) ?? throw new VpnException("VpnUnavailable");
        Connection? connection = null;
        try
        {
            connection = new(process, new RunnerProcessJob(process), access, controllerPort, secret, Path.Combine(home, cache));
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            // 通过子进程标准输入传递包含订阅地址的配置，避免凭据出现在命令行或配置文件中。
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(config).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                try
                {
                    if ((await ReadNodesAsync(connection, deadline.Token)).Length != 0)
                    {
                        connection.ClearProviderCache();
                        return connection;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, deadline.Token);
            }
            throw new VpnException("VpnUnavailable");
        }
        catch
        {
            if (connection is not null) connection.Dispose();
            else
            {
                if (!process.HasExited) process.Kill();
                process.Dispose();
            }
            throw;
        }
    }

    private string ReadAddress(Subscription subscription)
    {
        var plain = ProtectedData.Unprotect(subscription.Address, key, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static async Task<string[]> ReadNodesAsync(Connection active, CancellationToken token)
    {
        using var doc = await ReadJsonAsync(active.Controller, "providers/proxies/" + Provider, token);
        if (!doc.RootElement.TryGetProperty("proxies", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(item => item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                type.GetString() is not ("Direct" or "Reject" or "Pass" or "Compatible"))
            .Select(item => item.GetProperty("name").GetString()!)
            .Where(ValidNode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(300).ToArray();
    }

    // 直接解析 Mihomo 策略组批量测速结果，不逐节点创建代理进程或发起 HTTP 探测。
    internal static Dictionary<string, int> ParseBatchDelays(JsonElement result, IEnumerable<string> knownNodes)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Mihomo 批量测速响应格式无效。");
        var delays = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in knownNodes)
        {
            if (result.TryGetProperty(node, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var delay) && delay is > 0 and < 65535)
                delays[node] = delay;
        }
        return delays;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient controller, string path, CancellationToken token)
    {
        using var response = await controller.GetAsync(path, token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private void Reset()
    {
        foreach (var active in connections.Values) active.Dispose();
        connections.Clear();
        nodes = [];
        delayResults = null;
    }

    public void Dispose()
    {
        Reset();
        gate.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        private readonly RunnerProcessJob job;
        private readonly string cache;
        internal Process Process { get; }
        internal VpnProxyAccess Proxy { get; }
        internal HttpClient Controller { get; }
        internal DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

        internal Connection(Process process, RunnerProcessJob job, VpnProxyAccess proxy, int port, string secret, string cache)
        {
            Process = process;
            this.job = job;
            this.cache = cache;
            Proxy = proxy;
            Controller = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = new($"http://127.0.0.1:{port}/"), MaxResponseContentBufferSize = 1024 * 1024
            };
            Controller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        public void Dispose()
        {
            Controller.Dispose();
            job.Dispose();
            Process.WaitForExit(1000);
            Process.Dispose();
            ClearProviderCache();
        }

        internal void ClearProviderCache()
        {
            try { File.Delete(cache); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
