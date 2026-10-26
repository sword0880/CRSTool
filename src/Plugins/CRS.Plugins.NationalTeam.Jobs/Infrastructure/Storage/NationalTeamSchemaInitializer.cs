using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>根版本与初始化顺序在存储组合根管理；只读会话不调用初始化。</summary>
internal static class NationalTeamSchemaInitializer
{
    public static void Initialize(MarketDatabaseSession session,NationalTeamStores stores,Action initializeApprovals)
    {
        var version=Convert.ToInt32(session.Scalar("PRAGMA user_version"));
        if(version is not (0 or 2))throw new InvalidDataException("市场研究库格式不兼容，拒绝初始化或重建已有资料。");
        session.Execute("CREATE TABLE IF NOT EXISTS plugin_schema_migration(schema_version INTEGER PRIMARY KEY,applied_at TEXT NOT NULL,plugin_package_version TEXT NOT NULL); CREATE TABLE IF NOT EXISTS worker_business_command(request TEXT PRIMARY KEY,operation TEXT NOT NULL,completed_utc TEXT NOT NULL);");
        EvidenceSchema.Initialize(session);stores.Etf.InitializeSchema();
        stores.Calendar.InitializeSchema();stores.Equity.InitializeEquitySchema();stores.Activity.InitializeSchema();
        stores.Alerts.InitializeExitAlertSchema();stores.Nav.InitializeSchema();stores.Industry.InitializeSchema();stores.Index.InitializeSchema();
        initializeApprovals();
        session.Execute("INSERT OR IGNORE INTO plugin_schema_migration VALUES(2,$now,'2.0.0'); PRAGMA user_version=2;",("$now",session.Now));
    }
}
