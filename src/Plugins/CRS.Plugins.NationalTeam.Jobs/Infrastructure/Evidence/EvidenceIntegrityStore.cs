using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>原件完整性独立模块；只读核验与后台状态提交分开，不持有总仓储或跨请求核验缓存。</summary>
internal sealed class EvidenceIntegrityStore(IMarketRepositorySession session,Action markAlertPending,Action<string,long,int> recordStage)
{
    private SqliteConnection db => session.Connection;
    private RawEvidenceStore raw => session.RawEvidence;
    private Action? beforeWrite => session.BeforeWrite;
    private CancellationToken operationCancellation => session.OperationCancellation;
    private string Now => session.Now;
    private SqliteCommand Command(string sql,params (string,object?)[] args) => session.Command(sql,args);
    private void Execute(string sql,params (string,object?)[] args) => session.Execute(sql,args);
    /// <summary>纯只读完整性核验：发现未受信原件时拒绝返回可能误导的金融结果，不修改质量状态。</summary>
    public IReadOnlyDictionary<string,bool> Validate(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
    {
        var (bad,_,verified) = Scan(shareKey,includeCalendar,activityOnly,dashboardOnly,includeAttempts);
        if(bad.Count==0)return verified;
        // 已由后台作业标记 Q15 的原件交给各只读查询的现有 SQL 过滤/标红；
        // 后台尚未登记的新损坏必须失败关闭，不能在查询时偷偷写状态。
        var blocked=new HashSet<string>(StringComparer.Ordinal);
        using(var query=Command("SELECT evidence_id FROM quality_issue WHERE rule_id='Q15' AND status='Open'"))
        using(var reader=query.ExecuteReader())while(reader.Read())blocked.Add(reader.GetString(0));
        if(bad.Any(id=>!blocked.Contains(id)))
            throw new InvalidDataException("Evidence integrity failure; background reconciliation required.");
        return verified;
    }
    /// <summary>读取原始字节和规范化字节的哈希；仅在本次操作中缓存相同哈希的结果。</summary>
    private (List<string> Bad,List<string> Good,IReadOnlyDictionary<string,bool> Verified) Scan(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
    {
        var stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var bad=new List<string>();var good=new List<string>();
        var fetches=new List<(string Id,string Hash,string Original)>();
        using(var command=Command("""
            SELECT fetch_id,sha256,original_sha256 FROM source_fetch
            WHERE ($activity=0 OR fetch_id IN(SELECT fetch_id FROM market_activity_observation)
                OR fetch_id IN(SELECT fetch_id FROM exchange_calendar_day))
            AND ($dashboard=0 OR fetch_id IN(SELECT fetch_id FROM share_observation_revision)
                OR fetch_id IN(SELECT fetch_id FROM market_activity_observation)
                OR fetch_id IN(SELECT fetch_id FROM exchange_calendar_day)
                OR fetch_id IN(SELECT fetch_id FROM trading_day))
            AND ($market IS NULL OR fetch_id IN(SELECT fetch_id FROM share_observation_revision WHERE market=$market AND ticker=$ticker)
                OR ($calendar=1 AND (fetch_id IN(SELECT fetch_id FROM trading_day WHERE market=$market)
                    OR fetch_id IN(SELECT fetch_id FROM exchange_calendar_day WHERE market=$market)))
                OR ($attempts=1 AND fetch_id IN(
                    SELECT t.fetch_id FROM etf_collection_attempt a
                    JOIN etf_collection_transition t ON t.attempt_id=a.id
                    WHERE a.market=$market AND a.ticker=$ticker AND t.fetch_id IS NOT NULL
                    AND t.sequence=(SELECT max(sequence) FROM etf_collection_transition WHERE attempt_id=a.id))))
            """,("$market",shareKey?.Market),("$ticker",shareKey?.Ticker),("$calendar",includeCalendar?1:0),("$activity",activityOnly?1:0),("$dashboard",dashboardOnly?1:0),("$attempts",includeAttempts?1:0)))
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {
                operationCancellation.ThrowIfCancellationRequested();
                fetches.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));
            }
        recordStage("EvidenceSql",stageStarted,fetches.Count);stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var verified=raw.VerifyMany(fetches.SelectMany(x=>new[]{x.Hash,x.Original}),operationCancellation);
        // 此阶段包含共享句柄准入、完整读取及SHA核验；不能把它全称为物理磁盘耗时。
        recordStage("RawVerificationIncludingAdmission",stageStarted,verified.Count);stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var fetch in fetches)
        {
            operationCancellation.ThrowIfCancellationRequested();
            if(!verified[fetch.Hash]||!verified[fetch.Original])bad.Add(fetch.Id);else good.Add(fetch.Id);
        }
        recordStage("EvidenceClassification",stageStarted,fetches.Count);return (bad,good,verified);
    }
    /// <summary>仅由 Worker 持久后台完整性任务调用。状态变更和审计事件在单一事务中提交，重跑幂等。</summary>
    public void ReconcileEvidenceIntegrity()
    {
        var (bad,good,_)=Scan();
        ApplyIntegrityIds(bad,good);
    }
    public IReadOnlyList<IntegrityEvidence> CaptureIntegrityEvidence()
    {
        using var command=Command("SELECT fetch_id,sha256,original_sha256 FROM source_fetch");
        using var reader=command.ExecuteReader();var rows=new List<IntegrityEvidence>();
        while(reader.Read()){operationCancellation.ThrowIfCancellationRequested();rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2)));}
        return rows;
    }
    public void ApplyIntegrityEvidence(IReadOnlyList<IntegrityEvidence> snapshot,IReadOnlyDictionary<string,bool> verified)
    {
        var current=CaptureIntegrityEvidence().ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var bad=new List<string>();var good=new List<string>();
        foreach(var row in snapshot)
        {
            operationCancellation.ThrowIfCancellationRequested();
            if(!current.TryGetValue(row.Id,out var actual)||actual!=row)continue;
            if(!verified.TryGetValue(row.Hash,out var hash)||!verified.TryGetValue(row.OriginalHash,out var original))
                throw new InvalidDataException("原件批次核验结果不完整。");
            (hash&&original?good:bad).Add(row.Id);
        }
        ApplyIntegrityIds(bad,good);
    }
    private void ApplyIntegrityIds(List<string> bad,List<string> good)
    {
        var openIssues=new HashSet<string>(StringComparer.Ordinal);
        using(var command=Command("SELECT id FROM quality_issue WHERE rule_id='Q15' AND status='Open'"))
        using(var reader=command.ExecuteReader())while(reader.Read())openIssues.Add(reader.GetString(0));
        var damaged=bad.Where(id=>!openIssues.Contains("raw-"+id)).ToArray();
        var restored=good.Where(id=>openIssues.Contains("raw-"+id)).ToArray();
        if(damaged.Length==0&&restored.Length==0)return;
        beforeWrite?.Invoke();
        using var transaction=db.BeginTransaction();
        foreach(var id in damaged)
        {
            operationCancellation.ThrowIfCancellationRequested();
            Execute("INSERT INTO evidence_integrity_event VALUES($event,$id,'Damaged',$now)",("$event",Guid.NewGuid().ToString("N")),("$id",id),("$now",Now));
            Execute("INSERT INTO quality_issue VALUES($issue,'Q15','1',$id,'Critical','Open',$id,NULL,NULL) ON CONFLICT(id) DO UPDATE SET status='Open',reviewer=NULL,decision_time=NULL",("$issue","raw-"+id),("$id",id));
        }
        foreach(var id in restored)
        {
            operationCancellation.ThrowIfCancellationRequested();
            Execute("INSERT INTO evidence_integrity_event VALUES($event,$id,'RestoredOriginalHash',$now)",("$event",Guid.NewGuid().ToString("N")),("$id",id),("$now",Now));
            Execute("UPDATE quality_issue SET status='Resolved',reviewer='SHA256-reverified',decision_time=$now WHERE id=$id",("$now",Now),("$id","raw-"+id));
        }
        markAlertPending();operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
}
