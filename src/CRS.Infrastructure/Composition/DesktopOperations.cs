using CRS.Domain;
using CRS.Application.Abstractions;
using NLog;

namespace CRS.Infrastructure;

/// <summary>适配现有基础设施行为；窗口只使用 Core 契约。</summary>
public sealed class DesktopOperations(ICalculationRepository repository, string configPath,VaultManager? vault=null) : IDesktopOperations
{
    private readonly Action<Action>? guard=vault?.CaptureGuard();
    /// <summary>取得本次运行配置位置。</summary>
    public string ConfigPath => configPath;
    /// <summary>提供历史仓储端口。</summary>
    public ICalculationRepository Repository => repository;
    /// <summary>提供 NPOI 底稿导出适配器。</summary>
    public IReportExporter Reports { get; } = new ProtectedExporter(vault?.CaptureGuard());
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
    public void SaveCarry(string path, CalculationResult result)
    {
        void Write()=>VaultFiles.AtomicWrite(path,CarryFiles.Encode(CarryFiles.Create(result)),true);
        if(guard is null) Write(); else guard(Write);
    }
    /// <summary>创建独立 HTTP 会话请求官方报告，取消时不继续保存。</summary>
    public async Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = await new FlexClient(http).DownloadAsync(queryId, token, from, to, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // 完成网络请求后仍检查当前保险库会话；锁定后的旧下载不得继续落盘。
        void Write() {cancellation.ThrowIfCancellationRequested(); VaultFiles.AtomicWrite(path,bytes,true);}
        if(guard is null) Write(); else guard(Write);
    }
    /// <summary>记录固定失败类别，不记录原始错误正文。</summary>
    public void LogFailure(string exceptionType) => LogManager.GetCurrentClassLogger().Warn("操作失败；类别={0}", exceptionType);
    /// <summary>导出整个提交过程受会话门闩保护，临时文件只在用户明确选择的明文导出位置生成。</summary>
    private sealed class ProtectedExporter(Action<Action>? guard) : IReportExporter
    {
        public void Export(string path,CalculationResult result,IEnumerable<ReviewNote>? reviews=null)
        {
            void Write()
            {
                var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try {ExcelReports.Export(temporary,result,reviews); File.Move(temporary,path,true);}
                finally {if(File.Exists(temporary)) File.Delete(temporary);}
            }
            if(guard is null) Write(); else guard(Write);
        }
    }
}
