namespace CRS.Domain.Performance;

/// <summary>保留历史模型格式，同时解除单次计算与调用方可变数组之间的共享。</summary>
public static class PerformanceDataSnapshot
{
    public static PerformanceData Copy(PerformanceData data)=>data with
    {
        Accounts=[..data.Accounts],Events=[..data.Events],
        Valuations=data.Valuations.Select(v=>v with{Components=[..v.Components]}).ToArray(),
        Prices=[..data.Prices],Fx=[..data.Fx],Coverage=[..data.Coverage],Transfers=[..data.Transfers],Revisions=[..data.Revisions]
    };
}
