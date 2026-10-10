namespace CRS.Application;

/// <summary>历史运行索引，供应用和界面查询，保持现有存储字段语义。</summary>
public sealed record HistoryItem(string Id, int Year, string CreatedUtc, bool Complete, int Issues, string Broker = "未登记", string? ParentSnapshotId = null, bool IsAnnualAggregate = false, string AccountScope = "");

/// <summary>分页索引同时返回总数，避免界面把最近一百份误当作全部历史。</summary>
public sealed record HistoryPageResult(List<HistoryItem> Rows, int TotalCount, int PageNumber, int PageSize);

/// <summary>原件校验所需的文件名提示，不向前台暴露摘要解析或复算规则。</summary>
public sealed record OriginalFileRequirements(string[] Reports, string? OpeningFile);

/// <summary>与计算快照绑定的人工复核依据，保存操作由仓储契约提供。</summary>
public sealed record ReviewNote(string SnapshotId, string Category, string Evidence, string CreatedUtc);
