using CRS.Domain;
using CRS.Application.Abstractions;
using NLog;

namespace CRS.Infrastructure;

/// <summary>为桌面启动和验证入口装配应用用例，领域层无需知道具体实现。</summary>
public static class CalculationServices
{
    /// <summary>将汇率、导入、结转、证据与日志实现注入年度计算服务。</summary>
    public static CalculationService Create(IExchangeRateProvider rates, string broker = "IBKR", int year = 2025)
    {
        var log = LogManager.GetCurrentClassLogger();
        IBrokerImporter importer = broker switch { "IBKR" => new IbkrImporter(), "FUTU" => new FutuImporter(year), _ => throw new CrsException("不支持所选券商。") };
        return new CalculationService(rates, importer, new CarryValidator(), new FileCalculationEvidence(),
            message => log.Info(message));
    }
}
