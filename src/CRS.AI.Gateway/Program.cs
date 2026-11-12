using System.ServiceProcess;
using CRS.AI.Gateway.Hosting;

if (args is ["--service"])
{
    using var service = new WindowsGatewayService();
    ServiceBase.Run(service);
    return service.ExitCode;
}
return await GatewayCli.RunAsync(args);
