using CRS.Domain.Performance;
namespace CRS.Infrastructure;

/// <summary>兼容既有调用入口；各券商独立解析与投影。</summary>
internal static class PerformanceBrokerImporter
{
    internal static Dictionary<string,string> FutuAccountLabels(FrozenInput input)=>FutuPerformanceAdapter.AccountLabels(input);
    internal static PerformanceData Ibkr(FrozenInput input,CancellationToken cancellation)=>IbkrPerformanceAdapter.Read(input,cancellation);
    internal static PerformanceData Futu(FrozenInput input,CancellationToken cancellation)=>FutuPerformanceAdapter.Read(input,cancellation);
}
