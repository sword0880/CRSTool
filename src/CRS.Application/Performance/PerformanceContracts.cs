using CRS.Domain.Performance;

namespace CRS.Application.Performance;

public sealed record PerformanceDocument(string Hash,string Name,PerformanceData Data,byte[] Raw,string Format);
public sealed record PerformanceRun(string Id,string? ParentId,DateTimeOffset CreatedAt,string InputHash,PerformanceData Input,PerformanceResult Result)
{public string ResultSeal {get;init;}="";}
public sealed record PerformanceHistory(string Id,DateTimeOffset CreatedAt,DateOnly From,DateOnly To,string Currency,string Scope,string InputHash);
public sealed record PerformanceInputInfo(string Hash,string Name,bool Included);
public sealed record PerformanceSourceRevision(string Hash,bool Included,string Reason,DateTimeOffset CreatedAt);
public sealed record PerformanceWorkspace(PerformanceData Data,InvestmentPortfolio[] Portfolios,PerformanceHistory[] History)
{public PerformanceInputInfo[] Sources {get;init;}=[];}
/// <summary>绩效端口只暴露用例，桌面不接触密钥、数据库或文件解析器。</summary>
public interface IPerformanceUseCases
{
    Task<bool> FetchBenchmarkAsync(string symbol,PerformanceRequest request,CancellationToken cancellation=default,bool force=false)=>Task.FromResult(false);
    Task<PerformanceCurve> CurveAsync(string runId,string benchmark,CancellationToken cancellation=default)=>Task.FromResult(new PerformanceCurve([],benchmark,"暂无曲线资料。"));
    Task<PerformanceWorkspace> LoadAsync(CancellationToken cancellation=default);
    Task<bool> ImportAsync(string path,string broker,CancellationToken cancellation=default);
    Task<bool> ImportManualAsync(string json,CancellationToken cancellation=default);
    Task SavePortfolioAsync(InvestmentPortfolio portfolio,CancellationToken cancellation=default);
    Task RenameAccountAsync(string id,string alias,CancellationToken cancellation=default);
    Task ConfirmBaseCurrencyAsync(string id,string currency,CancellationToken cancellation=default);
    Task SetSourceIncludedAsync(string hash,bool included,string reason,CancellationToken cancellation=default);
    Task ReviseClassificationAsync(string eventId,InvestmentEventKind kind,bool confirmed,string reason,CancellationToken cancellation=default);
    Task ConfirmTransferAsync(string outgoingId,string incomingId,decimal tolerance,string reason,string toleranceCurrency="CNY",CancellationToken cancellation=default);
    Task<PerformanceRun> CalculateAsync(PerformanceRequest request,CancellationToken cancellation=default);
    Task<PerformanceRun> OpenRunAsync(string id,CancellationToken cancellation=default);
    Task<PerformanceRun> ReplayAsync(string id,CancellationToken cancellation=default);
    Task ExportAsync(string id,string path,bool encrypted,string password,SensitiveExportConsent? consent=null,CancellationToken cancellation=default);
}
public interface IPerformanceMarketData
{
    Task<PerformanceDocument> FetchAsync(string symbol,DateOnly from,DateOnly to,string currency,CancellationToken cancellation);
}
public interface IPerformanceStore
{
    PerformanceWorkspace Load();
    bool Import(PerformanceDocument document);
    void SavePortfolio(InvestmentPortfolio portfolio);
    void RenameAccount(string id,string alias);
    void ConfirmBaseCurrency(string id,string currency);
    void SourceRevision(PerformanceSourceRevision revision);
    void Revise(ClassificationRevision revision);
    void Transfer(TransferLink link);
    void SaveRun(PerformanceRun run);
    PerformanceRun OpenRun(string id);
}
public interface IPerformanceFiles
{
    PerformanceDocument Read(string path,string broker,CancellationToken cancellation);
    PerformanceDocument ReadManual(string json);
    string Hash(PerformanceData data);
    void Export(PerformanceRun run,string path,bool encrypted,string password,SensitiveExportConsent? consent,CancellationToken cancellation);
}
