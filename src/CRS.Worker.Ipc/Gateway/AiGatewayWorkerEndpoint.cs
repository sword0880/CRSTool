using System.IO.Pipes;
using System.Security.Principal;

namespace CRS.Worker.Ipc;

/// <summary>固定的 Gateway 服务身份和 Worker 专用管道。</summary>
public static class AiGatewayWorkerEndpoint
{
    public const string ServiceName = "CRSAiGateway";
    public const string PipeSuffix = ".ai-public";

    // 使用 sc.exe showsid CRSAiGateway 核验。服务使用 NT SERVICE\CRSAiGateway 作为登录账户。
    public const string ServiceSid = "S-1-5-80-3904832395-2943114554-1824942487-2770041460-2225003027";

    // 使用精确读写权限，避免 FILE_GENERIC_WRITE 隐含创建服务端实例的权限。
    internal const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
    internal static SecurityIdentifier Identity { get; } = new(ServiceSid);
}
