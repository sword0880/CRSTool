using CRS.Application.Performance;

namespace CRS.Infrastructure;

public sealed partial class VaultManager
{
    internal void ImportSharedPerformance(FrozenInput input,string broker,CancellationToken cancellation,Action<Action> originalSession)
    {
        lock(gate)
        {
            originalSession(()=>
            {
                var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this),active.Lifetime);
                var document=new PerformanceFiles(CaptureGuard(),exports).Read(input,broker,cancellation);
                cancellation.ThrowIfCancellationRequested();new PerformanceStore(active).Import(document);
            });
        }
    }
    /// <summary>绑定真实会话；online 决定是否提供行情适配器，绩效页面启用在线访问，锁定后取消。</summary>
    public IPerformanceUseCases CreatePerformanceUseCases(bool online=true)
    {
        lock(gate)
        {
            var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this),active.Lifetime);
            return new PerformanceUseCases(new PerformanceStore(active),new PerformanceFiles(CaptureGuard(),exports),active.Lifetime,online?new YahooPerformanceMarketData():null);
        }
    }
}
