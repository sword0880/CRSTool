namespace CRS.Domain;

/// <summary>跨年度结转数据契约；文件读取和摘要校验由基础设施负责。</summary>
public sealed record CarryDocument(int Version, int Year, string SourceSnapshotId, string[] Accounts,
    List<CostLot> Lots, List<RoundingState> Rounding);
