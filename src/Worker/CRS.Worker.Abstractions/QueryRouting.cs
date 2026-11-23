using System.Text.Json;
namespace CRS.Worker.Abstractions;

public enum BackgroundQueryWorkload { ShortRead, ResearchRead }
public sealed record BackgroundQueryRoute(string Operation,BackgroundQueryWorkload Workload);
/// <summary>启动时取得的静态公开读路由；宿主自行解析标量分类，调用方不能自报成本类别。</summary>
public sealed record BackgroundQueryRouting(string OperationProperty,string SnapshotProperty,BackgroundQueryRoute[] Routes)
{
    public BackgroundQueryRouting Validate()
    {
        static bool Property(string name)=>name.Length is >=1 and <=40&&name.All(char.IsAsciiLetterOrDigit);
        if(!Property(OperationProperty)||!Property(SnapshotProperty)||OperationProperty==SnapshotProperty||Routes.Length is <1 or >128
            ||Routes.Any(x=>x is null||!Enum.IsDefined(x.Workload)||x.Operation.Length is <1 or >40||x.Operation.Any(c=>c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            ||Routes.Select(x=>x.Operation).Distinct(StringComparer.Ordinal).Count()!=Routes.Length)throw new InvalidDataException("查询路由声明无效。");
        return this with{Routes=Routes.ToArray()};
    }
    public BackgroundQueryWorkload Classify(string payload)
    {
        using var doc=JsonDocument.Parse(payload,new(){MaxDepth=32});var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("查询参数无效。");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var property in root.EnumerateObject())if(!names.Add(property.Name))throw new InvalidDataException("查询参数重复。");
        if(root.TryGetProperty(SnapshotProperty,out var snapshot)&&snapshot.ValueKind==JsonValueKind.String&&snapshot.GetString() is {Length:>0})return BackgroundQueryWorkload.ShortRead;
        if(!root.TryGetProperty(OperationProperty,out var operation)||operation.ValueKind!=JsonValueKind.String)return BackgroundQueryWorkload.ResearchRead;
        return Routes.FirstOrDefault(x=>x.Operation==operation.GetString())?.Workload??BackgroundQueryWorkload.ResearchRead;
    }
}
public interface IBackgroundQueryRoutingSession
{
    BackgroundQueryRouting? DescribeQueryRouting();
}
/// <summary>停止接收查询并取消、排空脱离单次RPC的只读作业；维护结束前不得释放其资料连接。</summary>
public interface IBackgroundQueryLifetimeSession
{
    Task QuiesceQueriesAsync(CancellationToken token);
}
/// <summary>只约束公开查询；写入和采集仍由宿主持久执行组调度，不进入查询队列。</summary>
public static class BackgroundQueryBudgets
{
    public static int Concurrency(BackgroundQueryWorkload kind)=>kind==BackgroundQueryWorkload.ShortRead?2:1;
    public static int Waiting(BackgroundQueryWorkload kind)=>kind==BackgroundQueryWorkload.ShortRead?4:1;
    public static TimeSpan Wait(BackgroundQueryWorkload kind)=>TimeSpan.FromSeconds(kind==BackgroundQueryWorkload.ShortRead?1:2);
    public static TimeSpan Execution(BackgroundQueryWorkload kind)=>TimeSpan.FromSeconds(kind==BackgroundQueryWorkload.ShortRead?5:15);
}
