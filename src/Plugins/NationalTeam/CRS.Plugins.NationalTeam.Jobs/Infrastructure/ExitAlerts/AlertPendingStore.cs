// 预警待评价标记的单一持久化入口，由资料写入事务调用，确保已提交事实能触发后续评价。
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed class AlertPendingStore(IMarketRepositorySession session)
{
    public void Mark()=>session.Execute("INSERT INTO nt_alert_pending VALUES(1,$now) ON CONFLICT(id) DO UPDATE SET changed_at=excluded.changed_at",("$now",session.Now));
}
