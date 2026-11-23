using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EtfStore
{
    public void InitializeSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS worker_sync_cursor(id INTEGER PRIMARY KEY CHECK(id=1),checked_through TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS security_master(market TEXT, ticker TEXT, name TEXT NOT NULL, index_group TEXT NOT NULL, PRIMARY KEY(market,ticker));
            CREATE TABLE IF NOT EXISTS institution(institution_id TEXT PRIMARY KEY, legal_name TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS disclosure_evidence(id TEXT PRIMARY KEY, source_url TEXT NOT NULL, content_hash TEXT NOT NULL, report_as_of TEXT, source_published_utc TEXT, first_seen_utc TEXT NOT NULL, review_state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS watchlist_membership(market TEXT,ticker TEXT,active INTEGER NOT NULL,reason TEXT NOT NULL,review_status TEXT NOT NULL,evidence_id TEXT,PRIMARY KEY(market,ticker),FOREIGN KEY(market,ticker) REFERENCES security_master(market,ticker));
            CREATE TABLE IF NOT EXISTS watchlist_change(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,active INTEGER NOT NULL,reason TEXT NOT NULL,changed_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS etf_configuration_version(market TEXT,ticker TEXT,revision INTEGER NOT NULL,origin TEXT NOT NULL,PRIMARY KEY(market,ticker),FOREIGN KEY(market,ticker) REFERENCES security_master(market,ticker));
            CREATE TABLE IF NOT EXISTS etf_metadata_change(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,old_name TEXT,old_index TEXT,new_name TEXT NOT NULL,new_index TEXT NOT NULL,changed_utc TEXT NOT NULL);
            -- 20指表格约20行的可见区域；移除旧库容量触发器，保留全部ETF配置及历史数据。
            DROP TRIGGER IF EXISTS watchlist_insert_limit;
            DROP TRIGGER IF EXISTS watchlist_update_limit;
            CREATE TABLE IF NOT EXISTS etf_collection_attempt(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,trade_date TEXT NOT NULL,started_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_etf_collection_day ON etf_collection_attempt(market,ticker,trade_date);
            CREATE TABLE IF NOT EXISTS etf_collection_transition(sequence INTEGER PRIMARY KEY AUTOINCREMENT,attempt_id TEXT NOT NULL REFERENCES etf_collection_attempt(id),state TEXT NOT NULL,reason TEXT NOT NULL,fetch_id TEXT REFERENCES source_fetch(fetch_id),updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_etf_collection_transition ON etf_collection_transition(attempt_id,sequence);
            CREATE TABLE IF NOT EXISTS trading_day(market TEXT,trade_date TEXT,fetch_id TEXT NOT NULL,PRIMARY KEY(market,trade_date),FOREIGN KEY(fetch_id) REFERENCES source_fetch(fetch_id));
            CREATE TABLE IF NOT EXISTS share_observation_revision(revision_id TEXT PRIMARY KEY,market TEXT,ticker TEXT,trade_date TEXT,total_shares TEXT NOT NULL,original_value TEXT NOT NULL,original_unit TEXT NOT NULL,fetch_id TEXT NOT NULL,revision_no INTEGER NOT NULL,accepted_at TEXT,first_seen TEXT NOT NULL,status TEXT NOT NULL,supersedes_id TEXT,UNIQUE(market,ticker,trade_date,revision_no),FOREIGN KEY(fetch_id) REFERENCES source_fetch(fetch_id));
            CREATE INDEX IF NOT EXISTS ix_share_evidence_date ON share_observation_revision(trade_date DESC,revision_no DESC,revision_id);
            CREATE TABLE IF NOT EXISTS share_accepted_current(market TEXT,ticker TEXT,trade_date TEXT,revision_id TEXT NOT NULL,PRIMARY KEY(market,ticker,trade_date),FOREIGN KEY(revision_id) REFERENCES share_observation_revision(revision_id));
            CREATE TABLE IF NOT EXISTS sync_checkpoint(fetch_id TEXT PRIMARY KEY,status TEXT NOT NULL,accepted_count INTEGER NOT NULL);
            INSERT OR IGNORE INTO institution VALUES('huijin-investment','中央汇金投资有限责任公司'),('huijin-asset','中央汇金资产管理有限责任公司');
            """);
        // 内置观察候选默认全部监控；已有用户停用/修改仍由明确配置命令控制，不在重启时覆盖。
        using var seed=db.BeginTransaction();
        Execute("INSERT OR IGNORE INTO etf_configuration_version SELECT market,ticker,0,'原有配置' FROM security_master");
        foreach(var item in InitialEtfDirectory.Entries)
        {
            Execute("INSERT OR IGNORE INTO security_master VALUES($m,$t,$name,$index)",("$m",item.Market),("$t",item.Ticker),("$name",item.Name),("$index",item.Index));
            Execute("INSERT OR IGNORE INTO watchlist_membership VALUES($m,$t,1,'默认观察候选，名称和指数待核验','DataPilot',NULL)",("$m",item.Market),("$t",item.Ticker));
            Execute("INSERT OR IGNORE INTO etf_configuration_version VALUES($m,$t,0,$origin)",("$m",item.Market),("$t",item.Ticker),("$origin",InitialEtfDirectory.Origin));
        }
        seed.Commit();
    }
}
