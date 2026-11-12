using System.ComponentModel;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CRS.AI.Gateway.Hosting;
using CRS.AI.Infrastructure;
using CRS.Worker.Ipc;

namespace CRS.AI.Gateway.Audit;

internal static class GatewayAuditCommand
{
    public static int Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("请以管理员身份运行审计归档。");
            return 2;
        }

        try
        {
            if (!GatewayStopped())
            {
                Console.Error.WriteLine("请先停止 Gateway 服务，再归档审计文件。");
                return 1;
            }
            var storage = GatewayStorage.Installed();
            storage.Validate();
            var receipt = AiAuditArchive.Rotate(storage.AuditPath, storage.AuditMaintenancePath);
            Console.WriteLine(receipt is null ? "审计文件为空。" : JsonSerializer.Serialize(receipt));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidDataException or JsonException or InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine(error is InvalidDataException or JsonException
                ? "审计校验失败，保留原记录供核对。"
                : "审计归档未完成，请核对服务状态、文件权限及可用空间。");
            return 2;
        }
    }

    private static bool GatewayStopped()
    {
        using var service = new ServiceController(AiGatewayWorkerEndpoint.ServiceName);
        try { return service.Status == ServiceControllerStatus.Stopped; }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
        {
            // 支持服务已卸载、但审计目录仍保留的维护场景。
            return true;
        }
    }
}
