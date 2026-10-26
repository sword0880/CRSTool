using CRS.Plugins.NationalTeamWatch.Application;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private List<EtfQueryStage>? queryStages;
    // 每个只读会话由原有读取门槛独占；失败/取消后释放，不跨查询缓存原件或金融结果。
    private (T Result,IReadOnlyList<EtfQueryStage> Stages) ProfileQuery<T>(Func<T> read)
    {
        if(queryStages is not null)throw new InvalidOperationException("同一读取会话不能嵌套诊断。");
        queryStages=[];
        try{return(read(),queryStages.ToArray());}
        finally{queryStages=null;}
    }
    private void RecordQueryStage(string name,long started,int items)
        =>queryStages?.Add(new(name,System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,items));
    private void RecordQueryMeasurement(string name,double elapsedMs,int items)
        =>queryStages?.Add(new(name,elapsedMs,items));
}
