// 公开研究库的来源、质量问题及完整性事件表初始化，供各领域在同一存储会话中引用。
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class EvidenceSchema
{
    public static void Initialize(IMarketRepositorySession session)=>session.Execute("""
            CREATE TABLE IF NOT EXISTS source_fetch(fetch_id TEXT PRIMARY KEY,provider TEXT NOT NULL,retrieved_utc TEXT NOT NULL,source_published_utc TEXT,source_url TEXT NOT NULL,sha256 TEXT NOT NULL,original_sha256 TEXT NOT NULL,status TEXT NOT NULL,parser_version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS quality_issue(id TEXT PRIMARY KEY,rule_id TEXT NOT NULL,rule_version TEXT NOT NULL,target_id TEXT NOT NULL,severity TEXT NOT NULL,status TEXT NOT NULL,evidence_id TEXT NOT NULL,reviewer TEXT,decision_time TEXT);
            CREATE INDEX IF NOT EXISTS ix_quality_evidence_rule_status ON quality_issue(evidence_id,rule_id,status);
            CREATE TABLE IF NOT EXISTS evidence_integrity_event(id TEXT PRIMARY KEY,fetch_id TEXT NOT NULL,event TEXT NOT NULL,checked_at TEXT NOT NULL);
            """);
}
