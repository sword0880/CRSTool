using CRS.Application.Abstractions;
using CRS.Application.Performance;

namespace CRS.Infrastructure;

/// <summary>整套保险库存储适配器，包含认证、计算/绩效仓储、会话门闩及安全备份恢复。
/// 不暴露连接或SQL；切换实现时这些能力必须作为同一族提供，不能回退到另一提供方。
/// </summary>
public interface IVaultStorageBackend : IVaultService
{
    ICalculationRepository Repository { get; }
    IPerformanceUseCases CreatePerformanceUseCases(bool online=true);
    Action<Action> CaptureGuard();
    void Execute(Action action);
    void ImportSharedPerformance(FrozenInput input,string broker,CancellationToken cancellation,
        Action<Action> originalSession,BrokerSourceDocument? sourceFacts=null);
}
