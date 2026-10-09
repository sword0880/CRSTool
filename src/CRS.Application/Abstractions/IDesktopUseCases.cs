namespace CRS.Application.Abstractions;

/// <summary>前台应用入口，只暴露用例，不暴露数据库、解析器和输出适配器。</summary>
public interface IDesktopUseCases
{
    string ConfigPath { get; }
    string ReadQueryId();
    void LogFailure(string exceptionType);
    Task<DesktopCalculationOutcome> CalculateAsync(DesktopCalculationRequest request, CancellationToken cancellation);
    Task<List<HistoryItem>> HistoryAsync();
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
