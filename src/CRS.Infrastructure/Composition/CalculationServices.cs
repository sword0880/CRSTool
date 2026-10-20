using CRS.Domain;
using CRS.Application.Abstractions;
using NLog;

namespace CRS.Infrastructure;

/// <summary>为桌面启动和验证入口装配应用用例，领域层无需知道具体实现。</summary>
public static class CalculationServices
{
    /// <summary>将汇率、导入、结转、证据与日志实现注入年度计算服务。</summary>
    public static CalculationService Create(IExchangeRateProvider rates, string broker = "IBKR", int year = 2025)
        =>Create(rates,broker,year,null);
    internal static CalculationService Create(IExchangeRateProvider rates,string broker,int year,Action<FrozenInput,CancellationToken,List<string>,BrokerSourceDocument?>? sharedImport)
    {
        var log = LogManager.GetCurrentClassLogger();
        IBrokerImporter importer = broker switch { "IBKR" => new IbkrImporter {SharedImport=sharedImport}, "FUTU" => new FutuImporter(year) {SharedImport=sharedImport}, _ => throw new CrsException("不支持所选券商。") };
        return new CalculationService(rates, importer, new CarryValidator(), new FileCalculationEvidence(),
            message => log.Info(message));
    }
}
