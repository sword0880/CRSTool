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
        using var priority=raw.PrioritizeQueryVerification();
        using var reader=new MarketRepository(database.OpenReader(),marketCycleApprovalAuditKey.ToArray());
        return reader.database.ReadSnapshot(()=>query(reader),token);
    }
}
