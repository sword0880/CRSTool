using CRS.AI.Contracts;

namespace CRS.AI.Query;

/// <summary>为本机入口和协议适配器统一组装受审计保护的只读查询。</summary>
public static class AiReadGatewayFactory
{
    public static IAiReadGateway Create(IWorkerPublicReadClient worker, IAiReadAuditSink audit,
        AiReadPolicyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(audit);
        return new AiReadPolicyGateway(new AiReadQueryService(worker), audit, limits ?? new());
    }
}
