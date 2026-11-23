using System.Collections.Immutable;
using System.Text.Json;
using CRS.Domain.Performance;

namespace CRS.Application.Performance;

public sealed record ProjectionFactDelta(string Category,string RowKey,string Change,
    string? BeforeHash,string? AfterHash,ImmutableArray<string> ChangedFields);
public sealed record ProjectionComparison(string LeftRun,string RightRun,bool Comparable,bool SameFacts,
    bool VersionsFullyRecorded,string Reason,ImmutableArray<string> SourceHashes,
    ImmutableArray<BrokerImportProvenance> LeftVersions,ImmutableArray<BrokerImportProvenance> RightVersions,
    ImmutableArray<ProjectionFactDelta> Changes);

/// <summary>比较同原件集合的冻结投影；只读审计，不运行旧程序集、不重算或覆盖历史。</summary>
public static class ProjectionComparer
{
    private sealed record Row(string Category,string Key,JsonElement Value);
    public static ProjectionComparison Compare(PerformanceRun left,PerformanceRun right,CancellationToken cancellation=default)
    {
        Validate(left,cancellation);Validate(right,cancellation);
        var sources=Sources(left);var other=Sources(right);
        var same=sources.Length>0&&sources.All(IsHash)&&sources.SequenceEqual(other);
        var known=same&&Recorded(left,sources)&&Recorded(right,sources);
        var changes=same?CompareRows(Rows(left.Input,cancellation),Rows(right.Input,cancellation),cancellation):[];
        return new(left.Id,right.Id,same,same&&changes.IsEmpty,known,
            same?"同原件冻结输入比对；版本缺失保持未知，事实相同不代表金融正确性验收。":"原件摘要缺失、无效或集合不同，不能作为同源投影比对。",
            [..sources],Versions(left),Versions(right),changes);
    }
    private static void Validate(PerformanceRun run,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();PerformanceEvidence.Verify(run);
        if(PerformanceSnapshot.Identity(run.Input)!=run.InputHash)throw new CrsException("投影比对的历史输入摘要不一致。");
    }
    private static ImmutableArray<BrokerImportProvenance> Versions(PerformanceRun run)=>run.ImportProvenance.IsDefault?[]:run.ImportProvenance;
    private static bool IsHash(string value)=>value is {Length:64}&&value.All(char.IsAsciiHexDigit);
    private static bool Recorded(PerformanceRun run,string[] hashes)=>hashes.All(h=>Versions(run).Any(p=>p.SourceFileHash==h));
    private static string[] Sources(PerformanceRun run)
    {
        var d=run.Input;
        return d.Events.Select(x=>x.Source.DocumentId).Concat(d.Valuations.Select(x=>x.Source.DocumentId))
            .Concat(d.Prices.Select(x=>x.Source.DocumentId)).Concat(d.Fx.Select(x=>x.Source.DocumentId))
            .Concat(d.Coverage.Select(x=>x.Source.DocumentId)).Concat(Versions(run).Select(x=>x.SourceFileHash))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    private static Dictionary<string,Row> Rows(PerformanceData data,CancellationToken cancellation)
    {
        var rows=new Dictionary<string,Row>(StringComparer.Ordinal);
        void AddRows<T>(string name,IEnumerable<T> values,Func<T,string> key)=>Add(rows,name,values,key,cancellation);
        AddRows("Accounts",data.Accounts,x=>x.Id);AddRows("Events",data.Events,x=>x.Id);
        AddRows("Valuations",data.Valuations,x=>x.Id);AddRows("Prices",data.Prices,x=>x.Id);AddRows("Fx",data.Fx,x=>x.Id);
        AddRows("Coverage",data.Coverage,x=>JsonSerializer.Serialize(new{x.AccountId,x.From,x.To,x.Source}));
        AddRows("Transfers",data.Transfers,x=>x.Id);AddRows("Revisions",data.Revisions,x=>x.Id);
        AddRows("Model",new[]{data.Version},_=>"Version");return rows;
    }
    private static void Add<T>(Dictionary<string,Row> rows,string category,IEnumerable<T> values,Func<T,string> key,CancellationToken cancellation)
    {
        foreach(var value in values)
        {
            cancellation.ThrowIfCancellationRequested();
            var id=key(value);var identity=JsonSerializer.Serialize(new[]{category,id});
            if(rows.Count>=200000||!rows.TryAdd(identity,new(category,id,JsonSerializer.SerializeToElement(value))))
                throw new CrsException("投影比对记录超限或标识重复，不能猜测对应关系。");
        }
    }
    private static ImmutableArray<ProjectionFactDelta> CompareRows(Dictionary<string,Row> left,Dictionary<string,Row> right,CancellationToken token)
    {
        var changes=ImmutableArray.CreateBuilder<ProjectionFactDelta>();
        foreach(var id in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();left.TryGetValue(id,out var before);right.TryGetValue(id,out var after);
            var a=Hash(before);var b=Hash(after);if(a==b)continue;
            if(changes.Count>=10000)throw new CrsException("投影差异超过10000项，未返回截断的比对结论。");
            var row=after??before!;
            changes.Add(new(row.Category,row.Key,before is null?"Added":after is null?"Removed":"Changed",a,b,Fields(before,after)));
        }
        return changes.ToImmutable();
    }
    private static string? Hash(Row? row)=>row is null?null:CanonicalEvidence.Hash(row.Value);
    private static ImmutableArray<string> Fields(Row? before,Row? after)
    {
        if(before is null||after is null)return [];
        if(before.Value.ValueKind!=JsonValueKind.Object||after.Value.ValueKind!=JsonValueKind.Object)return ["Value"];
        var a=before.Value.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
        var b=after.Value.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value);
        return [..a.Keys.Union(b.Keys).Order(StringComparer.Ordinal).Where(k=>!a.ContainsKey(k)||!b.ContainsKey(k)||CanonicalEvidence.Hash(a[k])!=CanonicalEvidence.Hash(b[k]))];
    }
}
