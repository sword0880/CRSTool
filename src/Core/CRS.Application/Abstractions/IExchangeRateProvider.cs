namespace CRS.Application.Abstractions;

/// <summary>汇率查询端口，配置格式和来源校验由基础设施实现。</summary>
public interface IExchangeRateProvider
{
    /// <summary>列出年度配置；只提供单币种查找的适配器可保持空列表。</summary>
    List<AppliedRate> ForYear(int year) => [];
    /// <summary>当前汇率配置指纹，用于冻结运行元数据。</summary>
    string Fingerprint { get; }
    /// <summary>取得指定年度币种的汇率和来源，缺失时明确拒绝。</summary>
    AppliedRate Get(int year, string currency);
}
