namespace CRS.Application;

/// <summary>历史运行索引，供应用和界面查询，保持现有存储字段语义。</summary>
public sealed record HistoryItem(string Id, int Year, string CreatedUtc, bool Complete, int Issues, string Broker = "未登记");

/// <summary>与计算快照绑定的人工复核依据，保存操作由仓储契约提供。</summary>
public sealed record ReviewNote(string SnapshotId, string Category, string Evidence, string CreatedUtc);
