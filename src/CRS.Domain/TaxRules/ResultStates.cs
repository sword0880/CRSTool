using System.Text.Json.Serialization;

namespace CRS.Domain;

/// <summary>定义来源成本是否已经包含可归属买入费用，防止同一费用重复扣除。</summary>
public enum CostBasisMode
{
    /// <summary>来源分别提供成交本金和买入费用，入库后合并为总成本。</summary>
    PrincipalPlusFees,
    /// <summary>来源直接提供已经包含费用的剩余总成本。</summary>
    TotalCostIncludesFees,
    /// <summary>无法证明成本口径，禁止形成完整年度收益。</summary>
    Unknown
}

/// <summary>说明历史输入是否可以在本地恢复并重放。</summary>
public enum InputRecoveryMode
{
    /// <summary>依赖用户重新提供并校验原始文件。</summary>
    ReimportOriginalFiles,
    /// <summary>保存了足以重算的规范化本地快照。</summary>
    CanonicalLocalSnapshot,
    /// <summary>只有哈希或摘要，无法重算。</summary>
    Unavailable
}

/// <summary>表示计算域是否完成，而不是简单的成功/失败布尔值。</summary>
public enum CalculationStatus { Completed, Partial, Blocked }

/// <summary>表示年度输入覆盖和成本资料的完整程度。</summary>
public enum DataCompleteness { Confirmed, Unconfirmed, Incomplete }

/// <summary>表示券商收益、现金和持仓对账结果。</summary>
public enum ReconciliationStatus { Matched, Differences, NotVerifiable }

/// <summary>限定结果的使用场景，防止暂算结果被误当作可申报结论。</summary>
public enum UsageLabel { ReviewReady, Provisional, ReviewOnly }
