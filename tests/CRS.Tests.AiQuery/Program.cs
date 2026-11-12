using CRS.Tests.AiQuery.Acceptance;
using CRS.Tests.AiQuery.Checks;

switch (args)
{
    case ["--local-gateway"]:
        await LocalGatewayAcceptance.RunAsync();
        break;
    case ["--audit-archive"]:
        await AuditArchiveAcceptance.RunAsync();
        break;
    case []:
        await AiReadRegressionChecks.RunAsync();
        break;
    default:
        return await LiveWorkerAcceptance.RunAsync(args);
}
return 0;
