using System.ComponentModel;
using System.Text.Json;
using CRS.AI.Gateway.Audit;
using CRS.AI.Gateway.Transport;

namespace CRS.AI.Gateway.Hosting;

internal static class GatewayCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--archive-audit"]) return GatewayAuditCommand.Run();
        var client = new LocalGatewayClient(LocalGatewayProtocol.PipeName, verifyService: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (args is ["--call", _] or ["--call", _, _])
            {
                var json = args.Length == 3 ? args[2] : "{}";
                if (json.Length > 4096) throw new ArgumentException("查询参数超过长度上限。");
                using var arguments = JsonDocument.Parse(json);
                var response = await client.CallAsync(new(LocalGatewayProtocol.Version, Guid.NewGuid(),
                    args[1], arguments.RootElement), deadline.Token);
                Console.WriteLine(JsonSerializer.Serialize(response.Result));
                return response.Result.Status == "Rejected" ? 1 : 0;
            }
            if (args is ["--grant", _] or ["--revoke", _] or ["--status"])
            {
                var action = args[0] switch { "--grant" => "Grant", "--revoke" => "Revoke", _ => "Status" };
                var response = await client.SetAccessAsync(new(LocalGatewayProtocol.Version, Guid.NewGuid(),
                    action, args.Length == 2 ? args[1] : null), deadline.Token);
                Console.WriteLine(response.Code);
                return response.Code == "Ok" ? 0 : 1;
            }
            Console.Error.WriteLine("用法：--call <工具名> [JSON参数]，或以管理员身份运行 --status、--grant <SID>、--revoke <SID>、--archive-audit。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                      OperationCanceledException or JsonException or ArgumentException or Win32Exception)
        {
            var reason = error switch
            {
                UnauthorizedAccessException => "Windows 权限不足或服务身份不匹配。",
                Win32Exception => "无法核实 Windows 服务身份。",
                OperationCanceledException => "连接或等待响应超时。",
                IOException => "本机管道连接中断。",
                _ => "参数或服务响应格式无效。"
            };
            Console.Error.WriteLine("Gateway 请求失败。" + reason);
        }
        return 2;
    }
}
