using CRS.Domain;
using CRS.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CRS.Infrastructure;

/// <summary>适配现有基础设施行为；窗口只使用 Core 契约。</summary>
public sealed class DesktopOperations(ICalculationRepository repository, string configPath,IVaultStorageBackend? vault=null,
    OfficialAnnualExchangeRates? officialRates=null, ILoggerFactory? loggerFactory=null) : IDesktopOperations
{
    private readonly Action<Action>? guard=vault?.CaptureGuard();
    private readonly ILogger<DesktopOperations> log=loggerFactory?.CreateLogger<DesktopOperations>()??NullLogger<DesktopOperations>.Instance;
    /// <summary>取得本次运行配置位置。</summary>
    public string ConfigPath => configPath;
    /// <summary>提供历史仓储端口。</summary>
    public ICalculationRepository Repository => repository;
    /// <summary>提供 NPOI 底稿导出适配器。</summary>
    public IReportExporter Reports { get; } = new ExcelReportExporter(vault?.CaptureGuard());
    /// <summary>加载并校验当前汇率配置。</summary>
    public IExchangeRateProvider LoadRates() => new ExchangeRates(ConfigPath);
    public Task<IExchangeRateProvider> LoadRatesAsync(int year, CancellationToken cancellation, bool refresh = false)
        => officialRates?.LoadAsync(year, cancellation, refresh) ?? Task.Run(LoadRates, cancellation);
    public string RateFingerprint(int year) => officialRates?.CachedFingerprint(year) ?? LoadRates().Fingerprint;
    /// <summary>装配年度计算所需基础设施。</summary>
    public CalculationService CreateCalculator(IExchangeRateProvider rates, string broker = "IBKR", int year = 2025)
        =>CalculationServices.Create(rates,broker,year,null,loggerFactory:loggerFactory);

    public IReadOnlySet<string> ArchivedOriginalHashes(string[] hashes,CancellationToken token)
        => vault?.ArchivedOriginalHashes(hashes,guard!,token) ?? new HashSet<string>();

    public IDesktopCalculationSession BeginHistoricalCalculation(IExchangeRateProvider rates,string broker,int year,
        IReadOnlyList<HistoricalOriginalInput> originals)
    {
        var byName = originals.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        return BeginCalculation(rates, broker, year, byName);
    }

    public IDesktopCalculationSession BeginCalculation(IExchangeRateProvider rates, string broker, int year)
        => BeginCalculation(rates, broker, year, null);

    private IDesktopCalculationSession BeginCalculation(IExchangeRateProvider rates, string broker, int year,
        IReadOnlyDictionary<string, HistoricalOriginalInput>? historical)
    {
        FrozenInput ReadHistorical(string name, CancellationToken token)
        {
            var original = historical![name];
            var input = original.LocalPath is { } path
                ? FrozenInput.Read(path,original.Name,token)
                : vault?.ReadArchivedOriginal(original.Name,original.Sha256,guard!,token);
            if (input is null || !input.Sha256.Equals(original.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new CrsException($"「{original.Name}」缺失或内容与历史摘要不符，请选择对应原件。");
            return input;
        }

        Func<string, CancellationToken, FrozenInput>? readOriginal = historical is null ? null : ReadHistorical;
        if (vault is null)
            return new DesktopCalculationSession(CalculationServices.Create(rates, broker, year, null, readOriginal, loggerFactory:loggerFactory),
                (result, token, allowDuplicate) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (historical is null) repository.SaveImport(result, allowDuplicate);
                    else repository.Save(result, token);
                });

        var prepared = new List<CRS.Application.Performance.PerformanceDocument>();
        FrozenInput? incomeOriginal = null;
        long stagedBytes = 0;
        void Reserve(long bytes)
        {
            stagedBytes += bytes;
            if (stagedBytes > 128L * 1024 * 1024)
                throw new CrsException("本次导入超过 128 MiB 准备限额，请缩小导入批次。");
        }

        void PreparePerformance(FrozenInput input, CancellationToken token, List<string> _, BrokerSourceDocument? facts)
        {
            token.ThrowIfCancellationRequested();
            if (prepared.Any(x => x.Hash == input.Sha256)) return;
            var document = vault.PrepareSharedPerformance(input, broker, token, guard!, facts);
            prepared.Add(document);
            Reserve(document.Raw.LongLength + PerformanceCodec.Encode(document.Data).LongLength);
        }

        void PrepareIncome(FrozenInput input, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (historical is not null && historical[input.Name].LocalPath is null) return;
            Reserve(input.Length);
            incomeOriginal = input;
        }

        var calculator = CalculationServices.Create(rates, broker, year,
            historical is null ? PreparePerformance : null, readOriginal, PrepareIncome, loggerFactory);
        return new DesktopCalculationSession(calculator,
            (result, token, allowDuplicate) => vault.CommitCalculation(result, prepared, incomeOriginal, guard!, token, allowDuplicate),
            () =>
            {
                foreach (var document in prepared) System.Security.Cryptography.CryptographicOperations.ZeroMemory(document.Raw);
                prepared.Clear();
                incomeOriginal = null;
            });
    }
    /// <summary>只读取本地查询 ID，不读取服务令牌。</summary>
    public string ReadQueryId() => FlexConfiguration.ReadQueryId(ConfigPath);
    /// <summary>读取并验证本地结转。</summary>
    public CarryDocument ReadCarry(string path) => CarryFiles.Read(path);
    public IReadOnlyList<AccountPeriodEvidence> ReadAccountPeriods(string path,CancellationToken cancellation=default)=>AccountPeriodFiles.Read(path,cancellation);
    /// <summary>读取结构化抵免明细及实际凭证摘要。</summary>
    public ForeignCreditEvidence ReadForeignCredit(string path) => ForeignCreditFiles.Read(path);
    /// <summary>沿用现有结转格式保存可信期末 LOT。</summary>
    public void SaveCarry(string path, CalculationResult result,CancellationToken cancellation = default)
    {
        // 结转与 Excel 使用相同的取消和原子提交边界，不在编码期间持有会话门闩。
        cancellation.ThrowIfCancellationRequested();
        guard?.Invoke(()=>cancellation.ThrowIfCancellationRequested());
        var bytes=CarryFiles.Encode(CarryFiles.Create(result));
        AtomicExport.Write(path,output=>output.Write(bytes),cancellation,guard);
    }
    /// <summary>创建独立 HTTP 会话请求官方报告，取消时不继续保存。</summary>
    public async Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = await new FlexClient(http, loggerFactory?.CreateLogger<FlexClient>()).DownloadAsync(queryId, token, from, to, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // 完成网络请求后仍检查当前保险库会话；锁定后的旧下载不得继续落盘。
        void Write() {cancellation.ThrowIfCancellationRequested(); VaultFiles.AtomicWrite(path,bytes,true);}
        if(guard is null) Write(); else guard(Write);
    }
    /// <summary>记录固定失败类别，不记录原始错误正文。</summary>
    public void LogFailure(string exceptionType) => log.LogWarning("操作失败；类别={Category:l}", SensitiveDiagnostics.FailureCategory(exceptionType));
    /// <summary>统一后台导出策略只读检查，不提前创建明文文件。</summary>
    public void ValidateExportDestination(string path,bool plaintext,bool temporaryLocationConfirmed)=>ExportDestinationPolicy.Validate(path,plaintext,temporaryLocationConfirmed);
    /// <summary>枚举只允许固定事件；非法值也不把任意字符串写入日志。</summary>
    public void LogExportEvent(SensitiveExportOperation operation,SensitiveExportOutcome outcome)
    {
        if(!Enum.IsDefined(operation) || !Enum.IsDefined(outcome)) return;
        log.LogInformation("敏感文件操作；类型={Operation}；结果={Outcome}",operation,outcome);
    }
}
