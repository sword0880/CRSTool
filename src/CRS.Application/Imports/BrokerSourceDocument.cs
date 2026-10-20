using System.Collections.Immutable;

namespace CRS.Application;

/// <summary>不可变原始来源事实；保留未知记录/字段，不在这里做税务分类、净值补齐或金额推断。</summary>
public sealed record BrokerSourceDocument(string Broker,string SourceFileHash,string ImportBatchId,string ParserVersion,BrokerSourceRecord Root);

public sealed record BrokerSourceRecord(string Name,string SourceRow,ImmutableSortedDictionary<string,string> Fields,
    ImmutableArray<BrokerSourceRecord> Records,string Text)
{
    public bool HasElements=>!Records.IsEmpty;
    public BrokerSourceRecord? Section(string name)=>Records.FirstOrDefault(x=>x.Name==name);
    public IEnumerable<BrokerSourceRecord> Rows(string? name=null)=>name is null?Records:Records.Where(x=>x.Name==name);
}
