using CRS.Application.Performance;

namespace CRS.Infrastructure;

public sealed partial class VaultManager
{
    /// <summary>绑定当前真实会话，旧绩效用例不能在重新解锁后恢复权限。</summary>
    public IPerformanceUseCases CreatePerformanceUseCases()
    {
        lock(gate)
        {
            var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this),active.Lifetime);
            return new PerformanceUseCases(new PerformanceStore(active),new PerformanceFiles(CaptureGuard(),exports),active.Lifetime);
        }
    }
}
