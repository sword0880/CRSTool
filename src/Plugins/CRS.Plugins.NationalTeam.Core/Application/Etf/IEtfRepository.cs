namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>ETF用例所需的事实、导入与采集记录组合；不含持股、行业或预警能力。</summary>
public interface IEtfRepository : IEtfReadRepository, IEtfWriteRepository, IShareCollectionRepository;
