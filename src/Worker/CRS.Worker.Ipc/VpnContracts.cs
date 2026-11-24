using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CRS.Worker.Ipc;

public sealed record VpnSettingsStatus(bool Configured, bool EngineAvailable, string[] Nodes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubscriptionAddress { get; init; }

    // 与 Nodes 按位置对应，避免重复传输长节点名称；null 兼容未测速及旧版 Worker。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int?[]? Delays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? NodeTypes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VpnSubscriptionDetail[]? SubscriptionDetails { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LatencyTestFailed { get; init; }

    public string NodeLabel(string node)
    {
        var index = Array.IndexOf(Nodes, node);
        if (index < 0) return node;
        if (Delays is null || Delays.Length != Nodes.Length) return node + "（未测速）";
        return Delays[index] is > 0 and < 65535
            ? $"{node}（{Delays[index]} ms）" : node + "（未测得）";
    }
}

public sealed partial record VpnSubscriptionDetail(string Label, string Value)
{
    // 与 Mihomo 策略组共用，只排除以套餐字段开头并带有值的订阅项。
    public const string NamePattern =
        @"^\s*(?<label>剩余流量|套餐到期(?:时间)?|距离下次重置(?:剩余)?|下次重置(?:时间)?)\s*[:：]\s*(?<value>\S.*)$";

    [GeneratedRegex(NamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static VpnSubscriptionDetail? Parse(string name)
    {
        var match = NameRegex().Match(name);
        if (!match.Success) return null;
        var label = match.Groups["label"].Value switch
        {
            "剩余流量" => "剩余流量",
            "套餐到期" or "套餐到期时间" => "套餐到期",
            _ => "距离下次重置"
        };
        return new(label, match.Groups["value"].Value.Trim());
    }
}

public sealed class VpnProxyAccess
{
    public required int Port { get; init; }
    public required string Password { get; init; }

    public IWebProxy ToProxy()
    {
        if (Port is < 1024 or > 65535 || Password.Length != 48 || !Password.All(Uri.IsHexDigit))
            throw new InvalidDataException("VPN 连接信息无效。");
        return new WebProxy(new Uri($"http://127.0.0.1:{Port}"))
        {
            Credentials = new NetworkCredential("crs", Password)
        };
    }
}

public static class WorkerVpnClient
{
    public static async Task<VpnSettingsStatus> ReadAsync(BackgroundPipeClient client, CancellationToken token) =>
        Read<VpnSettingsStatus>(await client.QueryAsync(WorkerProtocol.GetVpnSettings, token));

    public static async Task<VpnSettingsStatus> ReadWithAddressAsync(BackgroundPipeClient client, CancellationToken token) =>
        Read<VpnSettingsStatus>(await SendAsync(client, WorkerProtocol.GetVpnSettings, "ShowSubscription", token));

    public static async Task<VpnSettingsStatus> SaveAsync(BackgroundPipeClient client, string address, CancellationToken token) =>
        Read<VpnSettingsStatus>(await SendAsync(client, WorkerProtocol.SaveVpnSettings, address, token));

    public static async Task<VpnSettingsStatus> RefreshAsync(BackgroundPipeClient client, CancellationToken token) =>
        Read<VpnSettingsStatus>(await client.QueryAsync(WorkerProtocol.RefreshVpnNodes, token));

    public static async Task<IWebProxy> OpenAsync(BackgroundPipeClient client, string node, CancellationToken token) =>
        Read<VpnProxyAccess>(await SendAsync(client, WorkerProtocol.OpenVpnRoute, node, token)).ToProxy();

    private static Task<BackgroundResponse> SendAsync(BackgroundPipeClient client, string command, string payload, CancellationToken token) =>
        client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), command, Payload: payload), token);

    private static T Read<T>(BackgroundResponse response)
    {
        if (response.Code == "Ok" && response.Payload is { } value && JsonSerializer.Deserialize<T>(value) is { } result)
            return result;
        throw new InvalidOperationException(response.Code switch
        {
            "Forbidden" => "当前 Windows 用户没有权限管理或使用此 VPN 订阅。",
            "VpnNotConfigured" => "请先在系统设置中保存 VPN 订阅。",
            "VpnEngineMissing" => "后台服务未安装 Mihomo。",
            "VpnNodeUnavailable" => "所选 VPN 节点已不可用，请刷新节点并重新选择。",
            "InvalidVpnSubscription" => "请输入完整的 HTTPS 订阅地址。",
            "VpnCapacity" => "VPN 节点连接数已达上限，请稍后重试。",
            "UnknownCommand" => "当前后台服务版本尚未支持 VPN 设置。",
            _ => "未能连接 VPN 或读取节点，请检查订阅和后台服务。"
        });
    }
}
