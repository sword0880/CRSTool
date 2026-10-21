using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private ExitAlertEvent? CurrentAlert(ExitAlertFinding finding,string version)
    {
        using var command=Command("SELECT payload FROM nt_risk_event WHERE rule_id=$r AND scope_id=$s AND rule_version=$v AND state NOT IN ('Resolved','RetractedByRevision','HistoricalEvidence','RuleSuperseded') ORDER BY julianday(updated_at) DESC LIMIT 1",("$r",finding.RuleId),("$s",finding.ScopeId),("$v",version));
        var json=command.ExecuteScalar() as string;return json is null?null:JsonSerializer.Deserialize<ExitAlertEvent>(json);
    }
    /// <summary>未知数据不解除风险；事件变更与各渠道Outbox同事务，重复评价不重复通知。</summary>
    private void ApplyAlertFinding(ExitAlertFinding finding,ExitAlertConfiguration configuration)
    {
        if(!configuration.Rules.Enabled)return;var previous=CurrentAlert(finding,configuration.Rules.Version);
        if(previous is null&&!finding.Triggered)return;
        var next=NextAlert(previous,finding,configuration);if(next is null)return;
        var row=next.Value.Event;var kind=next.Value.Kind;var notify=next.Value.Notify;
        beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
        Execute("""
            INSERT INTO nt_risk_event VALUES($id,$r,$s,$v,$first,$last,$lane,$state,$at,$p)
            ON CONFLICT(id) DO UPDATE SET last_date=excluded.last_date,state=excluded.state,updated_at=excluded.updated_at,payload=excluded.payload
            """,("$id",row.Id),("$r",row.RuleId),("$s",row.ScopeId),("$v",row.RuleVersion),("$first",row.FirstTradeDate.ToString("yyyy-MM-dd")),("$last",row.LastTradeDate.ToString("yyyy-MM-dd")),("$lane",row.Lane.ToString()),("$state",row.State),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
        Execute("INSERT INTO nt_risk_event_change VALUES($id,$n,$kind,$at,$p)",("$id",row.Id),("$n",row.ChangeNo),("$kind",kind),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
        if(finding.Triggered&&finding.InputSnapshots is {Count:>0})SaveAlertInputSnapshot(row,finding);
        if(notify)EnqueueAlertNotifications(row,kind,configuration.Routes);
        transaction.Commit();
    }
    private (ExitAlertEvent Event,string Kind,bool Notify)? NextAlert(ExitAlertEvent? previous,ExitAlertFinding finding,ExitAlertConfiguration configuration)
    {
        var rules=configuration.Rules;var now=Now;
        if(previous is null)
        {
            // 已关闭后同日重新出现也形成新轮次，不能与历史事件的序号1冲突。
            var episode=Scalar("SELECT count(*) FROM nt_risk_event WHERE rule_id=$r AND scope_id=$s",("$r",finding.RuleId),("$s",finding.ScopeId));
            var id=ExitAlertEvaluator.Hash(finding.RuleId+"|"+finding.ScopeId+"|"+rules.Version+"|"+finding.ObservationDate+"|"+finding.Fingerprint+"|"+episode);
            var state=finding.Lane==AlertLane.DataQuality?"PendingVerification":"Active";
            return (new(id,finding.RuleId,finding.ScopeId,rules.Version,finding.Lane,finding.Severity,finding.EvidenceKind,finding.ObservationDate,finding.ObservationDate,state,"Complete",finding.Preview,now,now,finding.Headline,finding.Explanation,finding.Fingerprint,finding.RevisionIds),"FirstObserved",CanNotify(finding,rules));
        }
        if(!finding.Complete&&!finding.Triggered)
        {
            if(previous.DataHealth=="Unknown")return null;
            return(previous with{DataHealth="Unknown",UpdatedAt=now,ChangeNo=previous.ChangeNo+1,ClearCount=0,LastClearDate=null},"DataUnavailable",false);
        }
        if(finding.Triggered)
        {
            if(previous.Fingerprint==finding.Fingerprint&&previous.Preview==finding.Preview&&previous.DataHealth=="Complete")return null;
            var revised=HasAcceptedReplacement(previous.RevisionIds);
            var material=revised||finding.Severity>previous.Severity||previous.Preview&&!finding.Preview;
            return(previous with{LastTradeDate=finding.ObservationDate,Severity=finding.Severity,EvidenceKind=finding.EvidenceKind,DataHealth="Complete",Preview=finding.Preview,
                UpdatedAt=now,Headline=finding.Headline,Explanation=finding.Explanation,Fingerprint=finding.Fingerprint,RevisionIds=finding.RevisionIds,ChangeNo=previous.ChangeNo+1,ClearCount=0,LastClearDate=null},material?"UpdatedEvidence":"ObservationUpdated",material&&CanNotify(finding,rules));
        }
        return ClearOrRetract(previous,finding,configuration);
    }
    private (ExitAlertEvent Event,string Kind,bool Notify)? ClearOrRetract(ExitAlertEvent previous,ExitAlertFinding finding,ExitAlertConfiguration configuration)
    {
        if(previous.LastClearDate==finding.ObservationDate)return null;
        if(finding.ObservationDate<previous.LastTradeDate)return null;
        var revised=HasAcceptedReplacement(previous.RevisionIds);
        // 两次解除观察必须是相邻的真实交易日，不能因停机或评价跳日累计到解除门槛。
        var markets=previous.ScopeId.StartsWith("monitored-sample:",StringComparison.Ordinal)?Configuration().Where(x=>x.Active).Select(x=>x.Market).Distinct():[previous.ScopeId.Split(':')[0]];
        var predecessor=markets.SelectMany(ReadCalendar).Where(x=>x.Valid&&x.Date<finding.ObservationDate).Select(x=>(DateOnly?)x.Date).Max();
        var clear=previous.LastClearDate==predecessor?previous.ClearCount+1:1;var resolved=revised||finding.Lane!=AlertLane.EtfOutflow||clear>=configuration.Rules.ClearObservations;
        var state=!resolved?previous.State:revised?"RetractedByRevision":finding.Lane==AlertLane.InstitutionEvidence?"HistoricalEvidence":"Resolved";
        var explanation=finding.Lane==AlertLane.InstitutionEvidence?finding.Explanation+" 当前正下界不再成立，不代表机构回补；历史证明仍保留。":finding.Explanation;
        return(previous with{State=state,DataHealth="Complete",UpdatedAt=Now,LastTradeDate=finding.ObservationDate,Explanation=explanation,Fingerprint=finding.Fingerprint,RevisionIds=finding.RevisionIds,ChangeNo=previous.ChangeNo+1,ClearCount=clear,LastClearDate=finding.ObservationDate},
            revised?"RetractedByRevision":resolved?"Resolved":"ClearPending",resolved&&configuration.Rules.NotifyResolved&&CanNotify(finding with{Severity=previous.Severity},configuration.Rules));
    }
    private static bool CanNotify(ExitAlertFinding finding,ExitAlertRules rules)=>finding.Lane!=AlertLane.EtfOutflow||finding.Severity>=AlertSeverity.Warning||rules.NotifyWatch;
    private bool HasAcceptedReplacement(IReadOnlyList<string> ids)=>ids.Any(id=>Convert.ToInt32(Scalar("""
        SELECT count(*) FROM share_observation_revision old JOIN share_accepted_current c
        ON c.market=old.market AND c.ticker=old.ticker AND c.trade_date=old.trade_date WHERE old.revision_id=$id AND c.revision_id<>old.revision_id
        """,("$id",id)))>0);
    /// <summary>换规则是版本替换，不能伪装成市场风险解除；保留历史事件和已受理记录。</summary>
    private void SupersedeAlertRules(string version)
    {
        using var command=Command("SELECT payload FROM nt_risk_event WHERE rule_version<>$v AND state IN ('Active','PendingVerification')",("$v",version));
        foreach(var previous in ReadJson<ExitAlertEvent>(command))
        {
            var row=previous with{State="RuleSuperseded",ChangeNo=previous.ChangeNo+1,UpdatedAt=Now};
            Execute("UPDATE nt_risk_event SET state=$s,updated_at=$at,payload=$p WHERE id=$id",("$s",row.State),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)),("$id",row.Id));
            Execute("INSERT INTO nt_risk_event_change VALUES($id,$n,'RuleSuperseded',$at,$p)",("$id",row.Id),("$n",row.ChangeNo),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
        }
    }
    private void EnqueueAlertNotifications(ExitAlertEvent row,string kind,IReadOnlyList<AlertNotificationRoute> routes)
    {
        // 更正、解除和新正式提醒替代尚未提交的旧提醒；历史已受理及结果未知记录原样保留。
        using(var command=Command("SELECT payload FROM nt_alert_outbox WHERE event_id=$id AND state IN ('Pending','Retry','Disabled','Unavailable','BlockedAcceptance')",("$id",row.Id)))
            foreach(var old in ReadJson<AlertOutboxItem>(command))WriteOutbox(old with{State="Superseded",LastCode="NewEventChange"});
        foreach(var route in routes)
        {
            var id=ExitAlertEvaluator.Hash(row.Id+"|"+row.ChangeNo+"|"+route.ChannelId+"|"+route.ProfileId);
            var subject=$"[{row.LaneText} · {row.SeverityText}] {row.Headline}";
            var body=$"{row.Headline}\n事件状态：{row.StateText}\n观察日期：{row.LastTradeDate:yyyy-MM-dd}\n首次发现：{row.FirstSeenAt}\n本次更新：{row.UpdatedAt}\n规则：{row.RuleVersion}\n证据：{row.EvidenceKind}\n变更：{kind}\n{row.Explanation}\n{row.HistoricalRevisionNote}\n{row.UsageText}\n这是日终公开数据观察，不能称为机构当日成交；渠道受理不证明用户已收到。";
            WriteOutbox(new(id,row.Id,row.ChangeNo,route.ChannelId,route.ProfileId,row.Preview?"BlockedAcceptance":"Pending",0,Now,"nt."+id.ToLowerInvariant()+"@crs.local",subject,body,Now,Kind:kind));
        }
    }
}
