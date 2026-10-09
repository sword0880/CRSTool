using System.Text.Json.Serialization;

namespace CRS.Domain;

public sealed class CostLot
{
    public required SecurityKey Key { get; init; }
    public required string Symbol { get; init; }
    public required DateTimeOffset BuyTime { get; init; }
    public bool HasOffset { get; init; }
    public required string RecordId { get; init; }
    public required string SourceFile { get; init; }
    public decimal Quantity { get; set; }
    /// <summary>来源给出的本金或含费总成本，具体含义由成本口径字段决定。</summary>
    public decimal Cost { get; set; }
    /// <summary>仅用于审计展示的买入费用披露，不一定参与成本扣减。</summary>
    public decimal Fee { get; set; }
    /// <summary>成本口径；未知口径的 LOT 不得参与完整收益计算。</summary>
    public CostBasisMode CostBasisMode { get; init; } = CostBasisMode.PrincipalPlusFees;
    /// <summary>展示用买入费用字段，避免报告把已含费用再次计入总成本。</summary>
    [JsonIgnore]
    public decimal FeeDisclosure => Fee;
    /// <summary>按成本口径取得唯一可从 FIFO 扣减的剩余总成本。</summary>
    [JsonIgnore]
    public decimal TotalCost => CostBasisMode switch
    {
        CostBasisMode.PrincipalPlusFees => Cost + Fee,
        CostBasisMode.TotalCostIncludesFees => Cost,
        _ => throw new CrsException("成本口径未知，不能计算总成本。")
    };
    /// <summary>复制批次余额，避免计算时修改调用方提供的期初记录。</summary>
    public CostLot Copy() => (CostLot)MemberwiseClone();
}
public sealed record RoundingState(SecurityKey Key, decimal CostDelta, decimal FeeDelta);
