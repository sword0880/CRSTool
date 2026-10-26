namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>固定证据含义；市场份额和研究推测不具有机构交易的证明力。</summary>
public enum EvidenceLevel { ResearchHypothesis, MarketObservation, DisclosedHolding }
public enum HoldingRelationship { DirectEquityHolding, StrategicControl, PublicChangeAnnouncement, FundHoldings, ViaEtfExposure }
public static class EvidenceSemantics
{
    public static string Text(EvidenceLevel level)=>level switch
    {
        EvidenceLevel.DisclosedHolding=>"A · 已披露持仓",
        EvidenceLevel.MarketObservation=>"B · 市场客观数据",
        _=>"C · 待核验研究线索"
    };
    public const string EtfBasis="ETF全市场份额；不代表任何机构持仓或买卖；资金流仅为同日NAV估算代理。";
    public const string SnapshotBasis="正式披露的报告期持仓快照；两期差额不代表具体日期的交易；不与ETF间接暴露合并。";
}
