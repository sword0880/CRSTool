using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository
{
    private sealed record SavedAlertInput(string EventId,int Sequence,ExitAlertEvent Original,AlertFundInput[] Inputs,NamedEtfHolding[] Holdings,string? Rechecked);
    /// <summary>冻结当次规则输入，历史更正不使用后来新增的监控成员、具名披露或其它规则版本。</summary>
    private void SaveAlertInputSnapshot(ExitAlertEvent row,ExitAlertFinding finding)
    {
        var inputs=finding.InputSnapshots??throw new InvalidDataException("触发事件缺少冻结输入。");
        Execute("INSERT INTO nt_alert_input_snapshot VALUES($id,$n,$inputs,$holdings,NULL)",("$id",row.Id),("$n",row.ChangeNo),
            ("$inputs",JsonSerializer.Serialize(finding.InputSnapshots)),("$holdings",JsonSerializer.Serialize(finding.HoldingSnapshots??[])));
        foreach(var id in inputs.SelectMany(x=>x.Points).Select(x=>x.RevisionId).Distinct())
            Execute("INSERT INTO nt_alert_snapshot_revision VALUES($id,$n,$r)",("$id",row.Id),("$n",row.ChangeNo),("$r",id));
        // 口径PDF和交易日历原件同样属于历史输入；完整性故障及原哈希恢复都须触发回算。
        var sources=new HashSet<string>(StringComparer.Ordinal);
        foreach(var input in inputs)
        {
            if(input.TradingDays.Count>0)
            {
                using var command=Command("SELECT DISTINCT fetch_id FROM trading_day WHERE market=$m AND trade_date BETWEEN $from AND $to",("$m",input.Key.Market),("$from",input.TradingDays[0].ToString("yyyy-MM-dd")),("$to",row.LastTradeDate.ToString("yyyy-MM-dd")));
                using var reader=command.ExecuteReader();while(reader.Read())sources.Add(reader.GetString(0));
            }
        }
        var hashes=inputs.Select(x=>x.Metadata?.EvidenceHash).Concat((finding.HoldingSnapshots??[]).Select(x=>x.DocumentHash)).Where(x=>x is not null).Distinct();
        foreach(var hash in hashes)
        {
            using var command=Command("SELECT DISTINCT fetch_id FROM nt_alert_document WHERE hash=$h",("$h",hash));using var reader=command.ExecuteReader();while(reader.Read())sources.Add(reader.GetString(0));
        }
        foreach(var source in sources)Execute("INSERT INTO nt_alert_snapshot_source VALUES($id,$n,$f)",("$id",row.Id),("$n",row.ChangeNo),("$f",source));
    }
    private List<SavedAlertInput> HistoricalInputs(string eventId)
    {
        using var command=Command("""
            SELECT s.event_id,s.sequence,h.payload,s.payload,s.holdings,s.rechecked FROM nt_alert_input_snapshot s
            JOIN nt_risk_event_change h ON h.event_id=s.event_id AND h.sequence=s.sequence WHERE s.event_id=$id ORDER BY s.sequence
            """,("$id",eventId));
        using var reader=command.ExecuteReader();var rows=new List<SavedAlertInput>();
        while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetInt32(1),JsonSerializer.Deserialize<ExitAlertEvent>(reader.GetString(2))!,
            JsonSerializer.Deserialize<AlertFundInput[]>(reader.GetString(3))!,JsonSerializer.Deserialize<NamedEtfHolding[]>(reader.GetString(4))!,reader.IsDBNull(5)?null:reader.GetString(5)));
        return rows;
    }
    /// <summary>已解除事件也检查后来修订；回算显式属于修订后视角，不改写原始首次发现或原记录。</summary>
    private void RecheckHistoricalAlertInputs(ExitAlertConfiguration configuration)
    {
        using var command=Command("""
            SELECT DISTINCT i.event_id FROM nt_alert_snapshot_revision i JOIN share_observation_revision old ON old.revision_id=i.revision_id
            JOIN share_accepted_current c ON c.market=old.market AND c.ticker=old.ticker AND c.trade_date=old.trade_date
            WHERE c.revision_id<>old.revision_id OR EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=old.fetch_id AND q.rule_id='Q15' AND q.status IN ('Open','Resolved'))
            OR EXISTS(SELECT 1 FROM share_observation_revision p WHERE p.market=old.market AND p.ticker=old.ticker AND p.trade_date=old.trade_date AND p.status='NeedsReview')
            UNION SELECT DISTINCT s.event_id FROM nt_alert_snapshot_source s JOIN quality_issue q ON q.evidence_id=s.fetch_id WHERE q.rule_id='Q15' AND q.status IN ('Open','Resolved')
            """);
        var affected=new List<string>();using(var reader=command.ExecuteReader())while(reader.Read())affected.Add(reader.GetString(0));
        var points=new Dictionary<EtfKey,List<SharePoint>>();
        foreach(var id in affected)
        {
            var current=AlertEvent(id);if(current is null)continue;
            var ruleJson=Scalar("SELECT payload FROM nt_alert_rule WHERE version=$v",("$v",current.RuleVersion)) as string;
            if(ruleJson is null)throw new InvalidDataException("历史规则版本缺失，拒绝以当前参数代算。");
            var rules=JsonSerializer.Deserialize<ExitAlertRules>(ruleJson)!;var snapshots=HistoricalInputs(id);
            var evaluated=snapshots.Select(snapshot=>(Snapshot:snapshot,Finding:RecomputeHistorical(snapshot,rules,points))).ToArray();
            var changed=evaluated.Where(x=>(x.Snapshot.Rechecked??x.Snapshot.Original.Fingerprint)!=x.Finding.Fingerprint).ToArray();
            if(changed.Length==0)continue;
            // 当前观察日已由正常生命周期完成更正时，只保存回算指纹，避免再发一封相同更正。
            var olderChanged=changed.Where(x=>x.Snapshot.Original.LastTradeDate<current.LastTradeDate).ToArray();
            beforeWrite?.Invoke();using var transaction=db.BeginTransaction();
            foreach(var item in changed)Execute("UPDATE nt_alert_input_snapshot SET rechecked=$f WHERE event_id=$id AND sequence=$n",
                ("$f",item.Finding.Fingerprint),("$id",id),("$n",item.Snapshot.Sequence));
            if(olderChanged.Length>0)
            {
                var unknown=evaluated.Any(x=>!x.Finding.Complete&&!x.Finding.Triggered);
                var stillTriggered=evaluated.Any(x=>x.Finding.Triggered);
                var note="历史份额修订或原件状态变化后，按原成员、原规则、原核验口径回算"+
                    string.Join("、",olderChanged.Select(x=>x.Snapshot.Original.LastTradeDate.ToString("yyyy-MM-dd")).Distinct())+
                    "。这是修订后视角，不代表当时已经知道新数据。"+
                    (unknown?"部分历史输入现已不足，不能据此确认或解除风险。":stillTriggered?"历史证据已更新，仍有原观察窗口满足条件。":"原事件全部已存触发窗口现均不满足条件，撤销原信号。");
                var row=current with{State=!unknown&&!stillTriggered?"RetractedByRevision":current.State,
                    HistoricalRevisionNote=note,ChangeNo=current.ChangeNo+1,UpdatedAt=Now,
                    HistoricalRevisionIds=evaluated.SelectMany(x=>x.Finding.RevisionIds).Distinct().Order().ToArray(),
                    DataHealth=current.State is "Active" or "PendingVerification"?current.DataHealth:unknown?"Unknown":"Complete"};
                Execute("UPDATE nt_risk_event SET state=$s,updated_at=$at,payload=$p WHERE id=$id",("$s",row.State),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)),("$id",id));
                Execute("INSERT INTO nt_risk_event_change VALUES($id,$n,'HistoricalCorrection',$at,$p)",("$id",id),("$n",row.ChangeNo),("$at",row.UpdatedAt),("$p",JsonSerializer.Serialize(row)));
                if(configuration.Rules.Enabled&&(configuration.Rules.NotifyWatch||row.Severity>=AlertSeverity.Warning||row.Lane==AlertLane.InstitutionEvidence))
                    EnqueueAlertNotifications(row,"HistoricalCorrection",configuration.Routes);
            }
            transaction.Commit();
        }
    }
    private ExitAlertFinding RecomputeHistorical(SavedAlertInput saved,ExitAlertRules rules,Dictionary<EtfKey,List<SharePoint>> points)
    {
        var original=saved.Original;var date=original.LastTradeDate;
        var inputs=saved.Inputs.Select(input=>
        {
            if(!points.TryGetValue(input.Key,out var current))points[input.Key]=current=ReadPoints(input.Key);
            var expected=input.TradingDays.Append(date).ToHashSet();var calendar=input.TradingDays.Count==0?[]:ReadCalendar(input.Key.Market).Where(x=>x.Date>=input.TradingDays[0]&&x.Date<=date).ToArray();
            return input with{Points=current.Where(x=>expected.Contains(x.Date)).ToArray(),
                CalendarVerified=input.CalendarVerified&&calendar.All(x=>x.Valid)&&calendar.Select(x=>x.Date).SequenceEqual(input.TradingDays),
                Metadata=input.Metadata is {} metadata&&!raw.Verify(metadata.EvidenceHash)?metadata with{OriginalConfirmed=false}:input.Metadata};
        }).ToArray();
        if(original.RuleId=="EtfShareContraction")return ExitAlertEvaluator.Single(inputs.Single(),date,rules,!original.Preview);
        if(original.RuleId=="SampleBreadth")
        {
            var window=int.Parse(original.ScopeId.Split(':')[1],System.Globalization.CultureInfo.InvariantCulture);
            return ExitAlertEvaluator.Cluster(inputs.Select(x=>(x,ExitAlertEvaluator.Windows(x,date,rules))).ToArray(),date,rules,!original.Preview,window);
        }
        var holdings=saved.Holdings.Select(x=>raw.Verify(x.DocumentHash)?x:x with{OriginalConfirmed=false}).ToArray();
        var proof=ExitAlertEvaluator.Minimum(holdings,inputs.Single(),date,saved.Inputs.Max(x=>x.EvaluatedAt));
        return new(original.RuleId,original.ScopeId,original.Lane,original.Severity,original.EvidenceKind,date,original.Headline,proof.Explanation,
            ExitAlertEvaluator.Hash(string.Join('|',proof.EvidenceIds)+"|"+proof.LowerBound),proof.EvidenceIds,proof.LowerBound>0,proof.State=="DerivedMinimumReduction",original.Preview);
    }
}
