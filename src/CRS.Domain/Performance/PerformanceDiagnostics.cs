namespace CRS.Domain.Performance;

/// <summary>本次运行的质量与证据累积器；步骤顺序决定指标当时的质量，不能事后统一覆盖。</summary>
internal sealed class PerformanceDiagnostics(PerformanceCalculationContext context)
{
    internal List<PerformanceIssue> Issues {get;}=[];
    internal List<PerformanceReconciliation> Reconciliations {get;}=[];
    internal List<PerformanceExposure> Exposure {get;}=[];
    internal PerformanceQuality Quality {get;private set;}=PerformanceQuality.Verified;
    internal string Alias(string id)=>context.Accounts.TryGetValue(id,out var a)?a.Alias:"范围资料";
    internal void Issue(string code,string account,string message,string action,PerformanceSource? source=null,bool blocks=true,string eventId="")
        =>Issues.Add(new(code,Alias(account),source?.DocumentId??"",source?.RowIdentity??"",message,action,blocks,eventId));
    internal void Lower(PerformanceQuality quality)=>Quality=Max(Quality,quality);
    internal static PerformanceQuality Max(PerformanceQuality a,PerformanceQuality b)=>a>b?a:b;
    internal decimal Tolerance(decimal value)=>Math.Max(context.Request.CurrencyUnit,Math.Max(context.Request.AbsoluteTolerance,Math.Abs(value)*context.Request.RelativeTolerance));
}
