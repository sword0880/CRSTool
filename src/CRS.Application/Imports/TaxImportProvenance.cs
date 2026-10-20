namespace CRS.Application;

/// <summary>税务来源版本审计；原始内容和金融事实仍由来源摘要/规范输入独立封存。</summary>
public sealed record TaxImportProvenance(int Version,string Broker,string SourceFileHash,string ImportBatchId,
    string ReaderVersion,string ProjectionVersion)
{
    internal static void Validate(IEnumerable<SourceReport> sources,IReadOnlyCollection<TaxImportProvenance>? values,string broker)
    {
        if(values is null)return; // 旧记录未知，不据当前代码补造。
        if(values.Count is <1 or >1000||values.Any(v=>v is null)||values.Select(x=>x.SourceFileHash).Distinct(StringComparer.Ordinal).Count()!=values.Count)
            throw new CrsException("税务来源版本依据为空、重复或超限。");
        var hashes=sources.Select(s=>s.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var value in values)
            if(value.Version!=1||value.Broker is not ("IBKR" or "FUTU")||(broker!="MULTI"&&value.Broker!=broker)||!Hash(value.SourceFileHash)
                ||value.ImportBatchId!=value.SourceFileHash||!hashes.Contains(value.SourceFileHash)
                ||!Label(value.ReaderVersion)||!Label(value.ProjectionVersion))
                throw new CrsException("税务来源版本依据与原件不一致或版本无效。");
    }
    private static bool Hash(string? value)=>value is{Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Label(string? value)=>value is{Length:>0 and <=128}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '_');
    internal static SnapshotList<TaxImportProvenance>? Merge(IEnumerable<TaxImportProvenance> values)
    {
        var rows=values.Distinct().OrderBy(v=>v.SourceFileHash,StringComparer.Ordinal).ToList();
        return rows.Count==0?null:(SnapshotList<TaxImportProvenance>)rows;
    }
}
