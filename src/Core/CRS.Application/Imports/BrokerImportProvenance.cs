namespace CRS.Application;

/// <summary>只描述实际执行过的来源读取/投影；旧资料缺失时保持未知，不以当前版本补填。</summary>
public sealed record BrokerImportProvenance(int Version,string SourceFileHash,string ImportBatchId,string Broker,
    string ReaderVersion,string ProjectionVersion,string DataSnapshotId)
{
    public void Validate()
    {
        static bool Hash(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool Label(string? value)=>value is {Length:>0 and <=128}&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '_');
        if(Version!=1||!Hash(SourceFileHash)||ImportBatchId!=SourceFileHash||!Hash(DataSnapshotId)
            ||Broker is not ("IBKR" or "FUTU")||!Label(ReaderVersion)||!Label(ProjectionVersion))
            throw new CrsException("券商解析版本依据无效或不受支持。");
    }
}
