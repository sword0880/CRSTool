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
    case ["--client-integration"]:
        // 使用管理员授权的临时管道验证 MCP 和 Responses 客户端。
        await LocalGatewayAcceptance.RunAsync(checkClient: true);
        break;
    case ["--model-settings"]:
        // 单独运行涉及当前 Windows 用户加密的模型配置验收。
        await AiModelSettingsChecks.RunAsync();
        break;
    case []:
        await AiAttachmentChecks.RunAsync();
        await AiReadRegressionChecks.RunAsync();
        await ResponsesApiChecks.RunAsync();
        await AdditionalModelProviderChecks.RunAsync();
        break;
    default:
        return await LiveWorkerAcceptance.RunAsync(args);
}
return 0;
