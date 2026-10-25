using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;
/// <summary>原文件读取、不可变存证及核验导出端口，应用层不直接操作文件。</summary>
public interface IRawEvidenceStore
{
    string Save(byte[] content);
    Task<byte[]> ReadImportFileAsync(string path,CancellationToken cancellationToken);
    Task ExportVerifiedAsync(string hash,string destination,IReadOnlyList<string> protectedDirectories,CancellationToken cancellationToken);
}
/// <summary>组合根装配完整存储族；业务用例依赖窄端口，所有方法均由提供方显式实现。</summary>
public interface IMarketRepository : IDisposable, IMarketOperations, IEtfReadRepository, IEtfWriteRepository, IShareCollectionRepository, IMarketActivityReadRepository, IMarketActivityWriteRepository, IEquityReadRepository, IEquityWriteRepository, IExitAlertReadRepository, IExitAlertWriteRepository, IEvidenceMaintenanceRepository, INavReadRepository, INavWriteRepository, IIndustryPeReadRepository, IIndustryPeWriteRepository
{
}
