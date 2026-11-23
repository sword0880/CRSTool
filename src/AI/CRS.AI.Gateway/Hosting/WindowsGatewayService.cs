using System.Security.Principal;
using System.ServiceProcess;
using CRS.AI.Gateway.Authorization;
using CRS.AI.Gateway.Transport;
using CRS.AI.Infrastructure;
using CRS.AI.Query;
using CRS.Worker.Ipc;

namespace CRS.AI.Gateway.Hosting;

internal sealed class WindowsGatewayService : ServiceBase
{
    private readonly CancellationTokenSource stop = new();
    private LocalAuthorizations? authorizations;
    private FileStream? auditLease;
    private Task? listener;

    public WindowsGatewayService()
    {
        ServiceName = AiGatewayWorkerEndpoint.ServiceName;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != AiGatewayWorkerEndpoint.ServiceSid)
            throw new UnauthorizedAccessException("Gateway 只能以专用 Windows 服务账户运行。");
        var storage = GatewayStorage.Installed();
        storage.Validate();
        auditLease = AiAuditMaintenanceLease.OpenService(storage.AuditMaintenancePath);
        authorizations = new LocalAuthorizations(new AuthorizationFile(storage.AuthorizationPath));
        var gateway = AiReadGatewayFactory.Create(WorkerPublicReadClient.ForGateway(),
            new AiAppendOnlyFileAuditSink(storage.AuditPath, storage.AuditMaintenancePath));
        listener = new LocalGatewayServer(gateway, authorizations, LocalGatewayProtocol.PipeName).RunAsync(stop.Token);
        if (listener.IsCompleted) listener.GetAwaiter().GetResult();
        _ = ObserveAsync(listener);
    }

    protected override void OnStop()
    {
        stop.Cancel();
        try { listener?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { auditLease?.Dispose(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            authorizations?.Dispose();
            auditLease?.Dispose();
            stop.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task ObserveAsync(Task running)
    {
        try { await running; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // 监听故障时交给 SCM 判定服务已结束，避免显示运行却没有安全入口。
            Environment.Exit(2);
        }
    }
}
