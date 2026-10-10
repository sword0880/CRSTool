namespace CRS.Application.Abstractions;

/// <summary>前台应用入口，只暴露用例，不暴露数据库、解析器和输出适配器。</summary>
public interface IDesktopUseCases
{
    string ConfigPath { get; }
    string ReadQueryId();
    void LogFailure(string exceptionType);
    Task<DesktopCalculationOutcome> CalculateAsync(DesktopCalculationRequest request, CancellationToken cancellation);
    Task<List<HistoryItem>> HistoryAsync();
    /// <summary>读取历史分页及总数。</summary>
    Task<HistoryPageResult> QueryHistoryAsync(int pageNumber, int pageSize);
    /// <summary>查询历史报告及期初原件要求。</summary>
    Task<OriginalFileRequirements> OriginalFileRequirementsAsync(string id);
    /// <summary>验证用户重新选择的原件，不修改历史结果。</summary>
    Task VerifyOriginalFilesAsync(string id, string[] files, string? opening, CancellationToken cancellation);
    /// <summary>冻结口径复算，等价验证通过后另存并保留父运行。</summary>
    Task<CalculationResult> ReplayAsync(string id, CancellationToken cancellation);
    /// <summary>确认账户归属后汇总同年度来源任务，沿用冻结汇率并另存来源关系。</summary>
    Task<CalculationResult> AggregateAnnualAsync(string[] sourceIds, bool ownershipConfirmed, CancellationToken cancellation);
    /// <summary>按原币事实复核抵免明细，另存派生任务并保留原结果。</summary>
    Task<CalculationResult> ReviewForeignCreditAsync(string id, string path, CancellationToken cancellation);
    Task<CalculationResult> LoadAsync(string id);
    Task<List<ReviewNote>> ReviewsAsync(string id);
    Task SaveReviewAsync(string id, string category, string evidence);
    Task ExportAsync(string path, CalculationResult result, string fingerprint);
    Task ExportCarryAsync(string path, CalculationResult result);
    Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation);
    Task<List<AppliedRate>> RatesAsync(int year);
    IReadOnlyList<Trade> Trades(CalculationResult result);
    IReadOnlyList<ReportSection> Sections(CalculationResult result, IReadOnlyList<ReviewNote> reviews);
}
