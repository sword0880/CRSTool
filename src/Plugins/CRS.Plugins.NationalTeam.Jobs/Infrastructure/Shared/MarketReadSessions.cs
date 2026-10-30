// 聚合仓储的独立只读会话入口，为每次查询建立一致性快照并沿用取消，读取不复用写连接。
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository : IMarketReadSessions
{
    public T Read<T>(Func<IMarketRepository,T> query,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var priority=CRS.Common.Concurrency.ReadWorkloadScope.Current==CRS.Common.Concurrency.ReadWorkloadKind.Background?null:raw.PrioritizeQueryVerification();
        using var reader=new MarketRepository(database.OpenReader(),marketCycleApprovalAuditKey.ToArray());
        return reader.database.ReadSnapshot(()=>query(reader),token);
    }
}
