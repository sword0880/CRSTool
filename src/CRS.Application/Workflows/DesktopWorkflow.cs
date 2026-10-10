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
    private readonly CancellationTokenSource lifetime=new();
    public void EndSession()=>lifetime.Cancel();
    public string ConfigPath => operations.ConfigPath;
    public string ReadQueryId() => operations.ReadQueryId();
    public void LogFailure(string exceptionType) => operations.LogFailure(exceptionType);
    public async Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token);
        await operations.DownloadAsync(queryId,token,from,to,path,linked.Token);
    }
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
        if (r.IsAnnualAggregate) sections.Add(new("年度汇总来源", r.AggregationSources.SelectMany(s => s.Accounts
            .Select(a => new { 来源任务 = s.SnapshotId, 券商 = s.Broker, 年度 = s.Year, 账户 = a })).ToList()));
        if (r.ForeignCreditAssessment is { } credit)
        {
            // 抵免明细与国家限额一起呈现，不用币种代替所得来源地。
            sections.Add(new("抵免国家限额", credit.Countries));
            sections.Add(new("抵免所得分配", credit.Evidence.Income));
            sections.Add(new("抵免凭证关系", credit.Evidence.Payments.SelectMany(t => t.Proofs.Select(p => new
            { t.Broker, t.Account, t.Country, t.Category, t.Currency, t.Amount, t.Authority, t.Reference, t.Exclusion, p.Role, p.Name, p.Sha256 })).ToList()));
        }
        if (r.Broker == "FUTU") sections.AddRange([new("年度收入主表", r.AnnualIncome), new("成交金额核对", r.TradeAmountChecks),
            new("资金进出明细", r.FundMovements), new("证券资产事件", r.SecurityMovements), new("资本收益汇总", FutuReporting.Capital(r)),
            new("入金汇总", FutuReporting.Deposits(r)), new("现金派息汇总", FutuReporting.DividendReceipts(r))]);
        sections.Add(new("提示", r.Warnings.Select(w => new { 说明 = w }).ToList()));
        return sections;
    }
    /// <summary>在后台完成计算，取消检查通过后才事务保存结果。</summary>
    public Task<DesktopCalculationOutcome> CalculateAsync(DesktopCalculationRequest request, CancellationToken cancellation) => Task.Run(() =>
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token); cancellation=linked.Token;
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
    /// <summary>分页查询在后台完成，不占用前台线程。</summary>
    public Task<HistoryPageResult> QueryHistoryAsync(int pageNumber, int pageSize)
        => Task.Run(() => operations.Repository.QueryHistory(pageNumber, pageSize));

    /// <summary>由应用层解释输入摘要，前台只选择对应的报告和期初原件。</summary>
    public Task<OriginalFileRequirements> OriginalFileRequirementsAsync(string id) => Task.Run(() =>
    {
        var snapshot = operations.Repository.Load(id);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot.CanonicalInputJson);
            var root = document.RootElement;
            var files = root.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray();
            if (files.Length == 0) throw new CrsException("该任务没有可校验的原始报告记录。");
            var opening = root.GetProperty("opening");
            return new OriginalFileRequirements(files, opening.ValueKind == System.Text.Json.JsonValueKind.Null
                ? null : opening.GetProperty("name").GetString());
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CrsException("历史记录缺少有效的原件摘要，仍可查看冻结结果。");
        }
    });

    /// <summary>校验原件仅使用历史摘要，取消或校验失败均不保存新运行。</summary>
    public Task VerifyOriginalFilesAsync(string id, string[] files, string? opening, CancellationToken cancellation)
        => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var snapshot = operations.Repository.Load(id);
            operations.CreateCalculator(new HistoricalOnlyRates(), snapshot.Broker, snapshot.Year)
                .VerifyOriginalFiles(snapshot, files, opening);
            cancellation.ThrowIfCancellationRequested();
        }, cancellation);

    /// <summary>复算沿用冻结汇率，严格输出比对后另存；旧结果和人工依据保持不变。</summary>
    public Task<CalculationResult> ReplayAsync(string id, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var snapshot = operations.Repository.Load(id);
        var replay = operations.CreateCalculator(new HistoricalOnlyRates(), snapshot.Broker, snapshot.Year).Replay(snapshot, cancellation);
        cancellation.ThrowIfCancellationRequested();
        operations.Repository.Save(replay);
        return replay;
    }, cancellation);

    /// <summary>在后台加载冻结来源并汇总；重复任务、未确认归属及取消均不保存。</summary>
    public Task<CalculationResult> AggregateAnnualAsync(string[] sourceIds, bool ownershipConfirmed, CancellationToken cancellation)
    {
        // 复制前台选择，避免等待后台期间集合变化改变本次汇总范围。
        var ids = sourceIds.ToArray();
        return Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ownershipConfirmed) throw new CrsException("请先确认所选账户均属于同一纳税人。");
            if (ids.Length is < 2 or > 100 || ids.Any(string.IsNullOrWhiteSpace)
                || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new CrsException("请选择 2 至 100 份互不重复的来源任务。");
            var results = new List<CalculationResult>();
            foreach (var id in ids.Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var result = operations.Repository.Load(id);
                if (result.SnapshotId != id) throw new CrsException("来源任务编号与冻结结果不一致。");
                results.Add(result);
            }
            var aggregate = new AnnualTaxAggregationService().Aggregate(results[0].TaxpayerScopeId, results[0].Year, results);
            cancellation.ThrowIfCancellationRequested();
            operations.Repository.Save(aggregate);
            return aggregate;
        }, cancellation);
    }

    /// <summary>抵免复核只采用冻结事实和汇率；成功后另存，错误或取消不写入历史。</summary>
    public Task<CalculationResult> ReviewForeignCreditAsync(string id, string path, CancellationToken cancellation) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var source = operations.Repository.Load(id);
        var sources = source.IsAnnualAggregate
            ? source.AggregationSources.Select(s => operations.Repository.Load(s.SnapshotId)).ToArray() : new[] { source };
        var assessment = ForeignCreditReview.Assess(operations.ReadForeignCredit(path), source.Year, sources, source.Rates);
        var result = ForeignCreditReview.Attach(source, assessment);
        cancellation.ThrowIfCancellationRequested();
        operations.Repository.Save(result);
        return result;
    }, cancellation);

    /// <summary>防止历史操作意外读取当前配置；复算实际汇率由历史执行上下文提供。</summary>
    private sealed class HistoricalOnlyRates : IExchangeRateProvider
    {
        public string Fingerprint => throw new CrsException("历史操作不能读取当前汇率配置。");
        public AppliedRate Get(int year, string currency) => throw new CrsException("历史操作不能使用当前汇率。");
    }
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
