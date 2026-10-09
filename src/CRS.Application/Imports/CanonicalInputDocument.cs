namespace CRS.Application;

/// <summary>规范化输入快照的版本化反序列化载体，不直接暴露为数据库表结构。</summary>
internal sealed class CanonicalInputDocument
{
    public string Schema { get; set; } = "";
    public string Broker { get; set; } = "IBKR";
    public List<AnnualIncome> AnnualIncome { get; set; } = [];
    public List<FundMovement> FundMovements { get; set; } = [];
    public List<TradeAmountCheck> TradeAmountChecks { get; set; } = [];
    public List<SecurityMovement> SecurityMovements { get; set; } = [];
    public List<Position> BeginningPositions { get; set; } = [];
    public List<Trade> Trades { get; set; } = [];
    public List<Trade> ReportedSales { get; set; } = [];
    public List<CashEvent> Cash { get; set; } = [];
    public List<CostLot> OpeningLots { get; set; } = [];
    public List<Position> Positions { get; set; } = [];
    public List<SourceReport> Sources { get; set; } = [];
    public List<RoundingState> OpeningRounding { get; set; } = [];
    public List<CashReconciliation> CashChecks { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<Issue> Issues { get; set; } = [];
    public CanonicalConfirmations Confirmations { get; set; } = new();

    /// <summary>把版本化 JSON 载体转换为领域输入集合，保持原始排序和问题清单。</summary>
    public ImportData ToImportData()
    {
        var data = new ImportData();
        data.Broker = Broker; data.AnnualIncome.AddRange(AnnualIncome); data.FundMovements.AddRange(FundMovements);
        data.TradeAmountChecks.AddRange(TradeAmountChecks); data.SecurityMovements.AddRange(SecurityMovements);
        data.BeginningPositions.AddRange(BeginningPositions);
        data.Trades.AddRange(Trades); data.ReportedSales.AddRange(ReportedSales); data.Cash.AddRange(Cash);
        data.OpeningLots.AddRange(OpeningLots); data.Positions.AddRange(Positions); data.Sources.AddRange(Sources);
        data.OpeningRounding.AddRange(OpeningRounding); data.CashChecks.AddRange(CashChecks);
        data.Warnings.AddRange(Warnings); data.Issues.AddRange(Issues);
        return data;
    }
}

/// <summary>快照中用户确认项的显式结构，避免从备注文本猜测确认状态。</summary>
internal sealed class CanonicalConfirmations
{
    public bool OpeningZero { get; set; }
    public bool ScopeConfirmed { get; set; }
    public string TaxpayerScopeId { get; set; } = "LOCAL_USER";
}
