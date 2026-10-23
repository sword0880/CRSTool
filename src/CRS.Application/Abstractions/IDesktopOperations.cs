namespace CRS.Application.Abstractions;

/// <summary>桌面工作流所需端口集合，由启动装配提供具体本地与网络实现。</summary>
public interface IDesktopOperations
{
    /// <summary>用户配置目录。</summary>
    string ConfigPath { get; }
    /// <summary>历史计算与复核仓储。</summary>
    ICalculationRepository Repository { get; }
    /// <summary>底稿输出端口。</summary>
    IReportExporter Reports { get; }
    /// <summary>读取年度汇率配置并返回经过校验的提供者。</summary>
    IExchangeRateProvider LoadRates();
    /// <summary>创建具有独立输入解析状态的计算用例。</summary>
    CalculationService CreateCalculator(IExchangeRateProvider rates, string broker = "IBKR", int year = 2025);
    /// <summary>准备计算及可选的同源绩效资料，成功后通过同一提交入口持久化。</summary>
    IDesktopCalculationSession BeginCalculation(IExchangeRateProvider rates, string broker, int year)
        => new DesktopCalculationSession(CreateCalculator(rates,broker,year),(result,token)=>
        {token.ThrowIfCancellationRequested();Repository.Save(result);});
    /// <summary>读取不含服务令牌的 Flex 查询配置。</summary>
    string ReadQueryId();
    /// <summary>校验并读取 C# 年度结转文件。</summary>
    CarryDocument ReadCarry(string path);
    /// <summary>限量读取账户期间清单和凭证，仅返回经用户核对的事实及摘要。</summary>
    IReadOnlyList<AccountPeriodEvidence> ReadAccountPeriods(string path,CancellationToken cancellation=default);
    /// <summary>读取抵免明细并对用户提供的凭证计算摘要，不保存凭证正文。</summary>
    ForeignCreditEvidence ReadForeignCredit(string path);
    /// <summary>将可信结果转换为年度结转并保存。</summary>
    void SaveCarry(string path, CalculationResult result, CancellationToken cancellation = default);
    /// <summary>请求官方 Flex 报告并保存，响应取消且不记录令牌。</summary>
    Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation);
    /// <summary>只记录异常类别，避免外部异常正文暴露敏感数据。</summary>
    void LogFailure(string exceptionType);
    /// <summary>检查导出／恢复目的地及明文临时目录策略，不创建任何文件。</summary>
    void ValidateExportDestination(string path,bool plaintext,bool temporaryLocationConfirmed);
    /// <summary>仅记录固定导出事件和结果，不接受路径、账户、金额或密码。</summary>
    void LogExportEvent(SensitiveExportOperation operation,SensitiveExportOutcome outcome);
}
