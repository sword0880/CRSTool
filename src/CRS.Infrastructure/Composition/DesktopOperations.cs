using CRS.Domain;
using CRS.Application.Abstractions;
using NLog;

namespace CRS.Infrastructure;

/// <summary>适配现有基础设施行为；窗口只使用 Core 契约。</summary>
public sealed class DesktopOperations(ICalculationRepository repository, string configPath) : IDesktopOperations
{
    /// <summary>取得本次运行配置位置。</summary>
    public string ConfigPath => configPath;
    /// <summary>提供历史仓储端口。</summary>
    public ICalculationRepository Repository => repository;
    /// <summary>提供 NPOI 底稿导出适配器。</summary>
    public IReportExporter Reports { get; } = new ExcelReportExporter();
    /// <summary>加载并校验当前汇率配置。</summary>
    public IExchangeRateProvider LoadRates() => new ExchangeRates(ConfigPath);
    /// <summary>装配年度计算所需基础设施。</summary>
    public CalculationService CreateCalculator(IExchangeRateProvider rates, string broker = "IBKR", int year = 2025) => CalculationServices.Create(rates, broker, year);
    /// <summary>只读取本地查询 ID，不读取服务令牌。</summary>
    public string ReadQueryId() => FlexConfiguration.ReadQueryId(ConfigPath);
    /// <summary>读取并验证本地结转。</summary>
    public CarryDocument ReadCarry(string path) => CarryFiles.Read(path);
    /// <summary>读取结构化抵免明细及实际凭证摘要。</summary>
    public ForeignCreditEvidence ReadForeignCredit(string path) => ForeignCreditFiles.Read(path);
    /// <summary>沿用现有结转格式保存可信期末 LOT。</summary>
    public void SaveCarry(string path, CalculationResult result) => File.WriteAllBytes(path, CarryFiles.Encode(CarryFiles.Create(result)));
    /// <summary>创建独立 HTTP 会话请求官方报告，取消时不继续保存。</summary>
    public async Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = await new FlexClient(http).DownloadAsync(queryId, token, from, to, cancellation);
        await File.WriteAllBytesAsync(path, bytes, cancellation);
    }
    /// <summary>记录固定失败类别，不记录原始错误正文。</summary>
    public void LogFailure(string exceptionType) => LogManager.GetCurrentClassLogger().Warn("操作失败；类别={0}", exceptionType);
}
