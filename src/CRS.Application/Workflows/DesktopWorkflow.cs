using CRS.Application.Abstractions;

namespace CRS.Application;

/// <summary>不可变桌面请求；前台只提交用户选择，不解析券商文件。</summary>
public sealed record DesktopCalculationRequest(string[] Files, int Year, string Broker,
    bool OpeningZero, bool ScopeConfirmed, bool UseCarry, string? OpeningPath, bool SaveCanonicalSnapshot);
public sealed record DesktopCalculationOutcome(CalculationResult Result, string RatesFingerprint);

/// <summary>WPF 前台的应用用例，集中计算、持久化和导出门槛。</summary>
public sealed class DesktopWorkflow(IDesktopOperations operations) : IDesktopUseCases
{
    private readonly ReviewUseCases reviews = new(operations.Repository);
    public string ConfigPath => operations.ConfigPath;
    public string ReadQueryId() => operations.ReadQueryId();
    public void LogFailure(string exceptionType) => operations.LogFailure(exceptionType);
    public Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation)
        => operations.DownloadAsync(queryId, token, from, to, path, cancellation);
    /// <summary>读取指定年度的全部已配置汇率，不猜测缺失的币种。</summary>
    public Task<List<AppliedRate>> RatesAsync(int year) => Task.Run(() => operations.LoadRates().ForYear(year));
    /// <summary>优先展示会话交易，完整快照可恢复历史交易；引用型历史不虚构明细。</summary>
    public IReadOnlyList<Trade> Trades(CalculationResult result)
    {
        if (result.SessionTrades.Count > 0) return result.SessionTrades;
        if (!result.IsReplayable || string.IsNullOrWhiteSpace(result.CanonicalInputJson)) return [];
        var document = System.Text.Json.JsonSerializer.Deserialize<CanonicalInputDocument>(result.CanonicalInputJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return document?.Trades.Where(t => t.Time.Year == result.Year).ToList() ?? [];
    }
    /// <summary>统一前台和导出的辅助汇总入口，展示层不重新聚合业务金额。</summary>
    public IReadOnlyList<ReportSection> Sections(CalculationResult r, IReadOnlyList<ReviewNote> reviews)
    {
        List<ReportSection> sections = [new("税务汇总", new[] { r.Summary }), new("待复核", r.Issues), new("FIFO 明细", r.Matches),
            new("卖出收益核对", r.PnlChecks), new("现金余额核对", r.CashChecks), new("汇率底稿", r.Rates), new("期末 LOT", r.EndingLots), new("人工复核", reviews)];
        if (r.Broker == "FUTU") sections.AddRange([new("年度收入主表", r.AnnualIncome), new("成交金额核对", r.TradeAmountChecks),
            new("资金进出明细", r.FundMovements), new("证券资产事件", r.SecurityMovements), new("资本收益汇总", FutuReporting.Capital(r)),
            new("入金汇总", FutuReporting.Deposits(r)), new("现金派息汇总", FutuReporting.DividendReceipts(r))]);
        sections.Add(new("提示", r.Warnings.Select(w => new { 说明 = w }).ToList()));
        return sections;
    }
    /// <summary>在后台完成计算，取消检查通过后才事务保存结果。</summary>
    public Task<DesktopCalculationOutcome> CalculateAsync(DesktopCalculationRequest request, CancellationToken cancellation) => Task.Run(() =>
    {
        var rates = operations.LoadRates();
        var carry = request.UseCarry && request.OpeningPath is not null ? operations.ReadCarry(request.OpeningPath) : null;
        var result = operations.CreateCalculator(rates, request.Broker, request.Year).Calculate(request.Files, request.Year,
            request.OpeningZero, request.ScopeConfirmed, request.UseCarry ? null : request.OpeningPath,
            carry, cancellation, request.SaveCanonicalSnapshot);
        cancellation.ThrowIfCancellationRequested();
        operations.Repository.Save(result);
        return new DesktopCalculationOutcome(result, rates.Fingerprint);
    }, cancellation);

    /// <summary>加载历史索引，避免数据库读取阻塞前台。</summary>
    public Task<List<HistoryItem>> HistoryAsync() => Task.Run(operations.Repository.History);
    /// <summary>读取冻结历史结果，保留原始汇率。</summary>
    public Task<CalculationResult> LoadAsync(string id) => Task.Run(() => operations.Repository.Load(id));
    /// <summary>读取快照对应的人工依据。</summary>
    public Task<List<ReviewNote>> ReviewsAsync(string id) => reviews.NotesAsync(id);
    /// <summary>保存依据，不修改原始税额或复核状态。</summary>
    public Task SaveReviewAsync(string id, string category, string evidence) => reviews.SaveAsync(id, category, evidence);
    /// <summary>导出前验证当前汇率指纹；历史结果使用冻结汇率。</summary>
    public Task ExportAsync(string path, CalculationResult result, string fingerprint) => Task.Run(() =>
    {
        if (fingerprint.Length > 0 && operations.LoadRates().Fingerprint != fingerprint)
            throw new CrsException("汇率配置已更改，请重新计算后导出。");
        operations.Reports.Export(path, result, operations.Repository.Reviews(result.SnapshotId));
    });
    /// <summary>只允许可信年末成本生成结转。</summary>
    public Task ExportCarryAsync(string path, CalculationResult result) => Task.Run(() =>
    {
        if (!result.CarryEligible) throw new CrsException("年末成本尚未核实，不能导出结转。");
        operations.SaveCarry(path, result);
    });
}
