namespace CRS.Application;

/// <summary>年度汇总来源保留券商与账户关系，相同账户编号在不同券商下不混淆。</summary>
public sealed record AggregationSource(string SnapshotId, string Broker, int Year, string[] Accounts);
