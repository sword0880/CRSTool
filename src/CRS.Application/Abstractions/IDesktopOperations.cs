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
    /// <summary>读取不含服务令牌的 Flex 查询配置。</summary>
    string ReadQueryId();
    /// <summary>校验并读取 C# 年度结转文件。</summary>
    CarryDocument ReadCarry(string path);
    /// <summary>将可信结果转换为年度结转并保存。</summary>
    void SaveCarry(string path, CalculationResult result);
    /// <summary>请求官方 Flex 报告并保存，响应取消且不记录令牌。</summary>
    Task DownloadAsync(string queryId, string token, DateOnly from, DateOnly to, string path, CancellationToken cancellation);
    /// <summary>只记录异常类别，避免外部异常正文暴露敏感数据。</summary>
    void LogFailure(string exceptionType);
}
