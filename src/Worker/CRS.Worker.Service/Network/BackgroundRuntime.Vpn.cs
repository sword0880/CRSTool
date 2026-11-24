using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;

namespace CRS.Worker.Service;

public sealed partial class BackgroundRuntime
{
    private async Task<BackgroundResponse> HandleVpnAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token)
    {
        BackgroundResponse Reply(string code, object? value = null) => new(WorkerProtocol.Version, request.RequestId, code,
            Payload: value is null ? null : JsonSerializer.Serialize(value));
        if (Vpn is null) return Reply("UnknownCommand");
        // 只有 VPN 设置页显式请求完整地址，节点下拉框仍使用摘要。
        var showAddress = request.Command == WorkerProtocol.GetVpnSettings && request.Payload == "ShowSubscription";
        var hasPayload = showAddress || request.Command is WorkerProtocol.SaveVpnSettings or WorkerProtocol.OpenVpnRoute;
        if (request.RequestId == Guid.Empty || hasPayload != (request.Payload is not null) ||
            request != new BackgroundRequest(WorkerProtocol.Version, request.RequestId, request.Command, Payload: request.Payload))
            return Reply("InvalidRequest");
        if (request.Command == WorkerProtocol.SaveVpnSettings && !caller.CanManage) return Reply("Forbidden");
        if (fault is not null || maintenance) return Reply("Maintenance");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        try
        {
            if (request.Command == WorkerProtocol.OpenVpnRoute)
                return Reply("Ok", await Vpn.OpenAsync(request.Payload!, caller.Sid, caller.CanManage, deadline.Token));
            var update = request.Command == WorkerProtocol.SaveVpnSettings ? request.Payload : null;
            return Reply("Ok", await Vpn.SettingsAsync(caller.Sid, caller.CanManage, update,
                request.Command == WorkerProtocol.RefreshVpnNodes, deadline.Token, showAddress));
        }
        catch (WorkerVpn.VpnException error) { return Reply(error.Code); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Reply("VpnUnavailable"); }
        catch (Exception error) when (VpnFailure(error)) { return Reply("VpnUnavailable"); }
    }

    private static bool IsAllowedVpnSelection(BackgroundJobDescriptor descriptor, string? node) =>
        node is null || descriptor.SupportsVpn && (node.Length == 0 || WorkerVpn.ValidNode(node));

    private async Task<BackgroundJobResult> ExecuteVpnJobAsync(HostedBackgroundJob handler, TaskDefinition plan, CancellationToken token)
    {
        if (handler.Session is not IProxyBackgroundSession routed)
            return new("Failed", "任务版本不支持所选 VPN 节点。");
        if (string.IsNullOrEmpty(plan.VpnNode)) return await routed.ExecuteViaProxyAsync(plan.JobId, null, token);
        if (Vpn is null) return new("Failed", "后台服务的 VPN 功能不可用。");
        VpnProxyAccess route;
        try { route = await Vpn.OpenAsync(plan.VpnNode, null, true, token); }
        catch (WorkerVpn.VpnException error)
        {
            return new("Failed", error.Code switch
            {
                "VpnEngineMissing" => "后台服务未安装 Mihomo。",
                "VpnNotConfigured" => "系统设置中尚未配置 VPN 订阅。",
                "VpnNodeUnavailable" => "所选 VPN 节点不可用，请刷新并重新选择。",
                _ => "无法连接所选 VPN 节点。"
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("Failed", "VPN 连接超时。"); }
        catch (Exception error) when (VpnFailure(error)) { return new("Failed", "无法连接所选 VPN 节点。"); }
        return await routed.ExecuteViaProxyAsync(plan.JobId, route.ToProxy(), token);
    }

    private static bool VpnFailure(Exception error) =>
        error is IOException or HttpRequestException or UnauthorizedAccessException or CryptographicException or JsonException or
            InvalidDataException or Win32Exception or InvalidOperationException or System.Net.Sockets.SocketException;
}
