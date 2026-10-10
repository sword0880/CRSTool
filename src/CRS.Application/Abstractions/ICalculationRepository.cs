namespace CRS.Application.Abstractions;

/// <summary>计算历史与复核仓储端口，隐藏 SQLite 结构。</summary>
public interface ICalculationRepository
{
    /// <summary>事务保存新运行及可用结转，不覆盖既有运行。</summary>
    void Save(CalculationResult result);
    /// <summary>查询最近的运行索引。</summary>
    List<HistoryItem> History();
    /// <summary>稳定排序并分页查询运行索引，页码从一开始。</summary>
    HistoryPageResult QueryHistory(int pageNumber, int pageSize);
    /// <summary>恢复指定运行的冻结结果。</summary>
    CalculationResult Load(string id);
    /// <summary>追加人工复核依据，不改变原始金额。</summary>
    void AddReview(string id, string category, string evidence);
    /// <summary>查询当前运行的复核依据。</summary>
    List<ReviewNote> Reviews(string id);
}
