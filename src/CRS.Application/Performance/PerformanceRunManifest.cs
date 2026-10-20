namespace CRS.Application.Performance;

public sealed record PerformanceDatasetEvidence(string Kind,string SourceHash,string[] Locators);
public sealed record PerformanceRunManifest(string RunId,string? ParentId,DateTimeOffset CreatedAt,string InputSnapshotId,
    string AlgorithmVersion,string CostMethodVersion,int DataModelVersion,string NormalizationVersion,string[] SourceHashes,
    PerformanceDatasetEvidence[] ExternalDatasets,string ResultSeal,string[] BlockingReasons)
{public System.Collections.Immutable.ImmutableArray<BrokerImportProvenance> ImportProvenance {get;init;}=[];}

/// <summary>从冻结运行生成只读清单，不改写历史。原版未记录的解析版本不推测补填。</summary>
public static class PerformanceRunManifests
{
    public static PerformanceRunManifest From(PerformanceRun run)
    {
        PerformanceEvidence.Verify(run);
        if(PerformanceSnapshot.Identity(run.Input)!=run.InputHash)throw new CrsException("绩效输入快照摘要不一致。");
        var d=run.Input;
        var sources=d.Events.Select(e=>e.Source).Concat(d.Valuations.Select(v=>v.Source)).Concat(d.Prices.Select(p=>p.Source))
            .Concat(d.Fx.Select(f=>f.Source)).Concat(d.Coverage.Select(c=>c.Source)).Select(s=>s.DocumentId).Distinct().Order(StringComparer.Ordinal).ToArray();
        var external=d.Prices.Select(p=>(Kind:p.Market is "INDEX" or "INDEX_FX"?p.Market:"PRICE",p.Source))
            .Concat(d.Fx.Select(f=>(Kind:"FX",f.Source))).GroupBy(x=>(x.Kind,x.Source.DocumentId))
            .OrderBy(g=>g.Key.Kind,StringComparer.Ordinal).ThenBy(g=>g.Key.DocumentId,StringComparer.Ordinal)
            .Select(g=>new PerformanceDatasetEvidence(g.Key.Kind,g.Key.DocumentId,g.Select(x=>x.Source.RowIdentity).Distinct().Order(StringComparer.Ordinal).ToArray())).ToArray();
        var provenance=run.ImportProvenance.IsDefault?[]:run.ImportProvenance;
        var allSources=sources.Concat(provenance.Select(p=>p.SourceFileHash)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var normalization=provenance.IsEmpty?"Unrecorded: frozen facts are authoritative":sources.Except(provenance.Select(p=>p.SourceFileHash)).Any()?"PartiallyRecorded: see ImportProvenance; frozen facts are authoritative":"Recorded: see ImportProvenance; frozen facts are authoritative";
        return new(run.Id,run.ParentId,run.CreatedAt,run.InputHash,run.Result.EngineVersion,run.Result.CostMethodVersion,d.Version,
            normalization,allSources,external,run.ResultSeal,run.Result.Issues.Where(i=>i.BlocksReturns).Select(i=>i.Code).Distinct().ToArray()){ImportProvenance=provenance};
    }
}
