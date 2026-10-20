using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>样本名单及有效期由研究者预先固定；不能按事后收益或触发结果挑选成员。</summary>
public sealed record AlertReplayMember(AlertFundInput Input,DateOnly ActiveFrom,DateOnly ActiveThrough);
public sealed record AlertReplayRequest(DateOnly From,DateOnly Through,DateTimeOffset KnowledgeCutoff,
    ExitAlertRules Rules,IReadOnlyList<AlertReplayMember> Members,IReadOnlyList<NamedEtfHolding> Holdings,string SampleBasis);
public sealed record AlertReplayCoverage(EtfKey Key,int RegisteredDays,int AcceptedDays,int Valid5,int Valid20,decimal? CoveragePercent,bool CalendarVerified);
public sealed record AlertReplayDay(DateOnly Date,IReadOnlyList<ExitAlertFinding> Findings);
public sealed record AlertReplayFirst(string RuleId,string ScopeId,AlertSeverity Severity,DateOnly Date);
public sealed record AlertReplayReport(string Mode,string TimingProof,string RuleVersion,string InputFingerprint,
    string Limitations,IReadOnlyList<AlertReplayCoverage> Coverage,IReadOnlyList<AlertReplayFirst> FirstTriggers,
    IReadOnlyList<AlertReplayDay> Days,AlertReplayRequest FrozenInputs);

/// <summary>只读研究回放：复用正式纯规则，不写事件或Outbox，不把回填事实宣称为当时可通知。</summary>
public static class ExitAlertReplay
{
    public static AlertReplayReport Run(AlertReplayRequest request)
    {
        request.Rules.Validate();
        var cutoffDate=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(request.KnowledgeCutoff,"China Standard Time").DateTime);
        if(request.From>request.Through||request.Through.DayNumber-request.From.DayNumber>3660||request.Through>cutoffDate
            ||request.Members.Count is <1 or >100||string.IsNullOrWhiteSpace(request.SampleBasis)
            ||request.Members.Select(x=>x.Input.Key).Distinct().Count()!=request.Members.Count
            ||request.Members.Any(x=>x.ActiveFrom>x.ActiveThrough||x.Input.Points.GroupBy(p=>p.Date).Any(g=>g.Count()>1)))
            throw new InvalidDataException("回放区间、固定样本说明、成员有效期或份额日期无效。");
        // 对外报告与计算共享同一份冻结副本，调用者之后修改列表不能改变复算输入。
        var frozen=JsonSerializer.Deserialize<AlertReplayRequest>(JsonSerializer.Serialize(request))!;
        var coverage=new List<AlertReplayCoverage>();var days=new SortedDictionary<DateOnly,List<ExitAlertFinding>>();
        var windows=new Dictionary<(EtfKey Key,DateOnly Date),AlertWindows>();
        foreach(var member in frozen.Members)
        {
            var input=member.Input;
            var dates=input.TradingDays.Distinct().Order().Where(d=>d>=request.From&&d<=request.Through&&d>=member.ActiveFrom&&d<=member.ActiveThrough).ToArray();
            int valid5=0,valid20=0;
            foreach(var date in dates)
            {
                // 历史日的知识时点取北京时间日末，且不超过调用者的全局截止时间。
                var end=new DateTimeOffset(date.ToDateTime(TimeOnly.MaxValue),TimeSpan.FromHours(8));
                var cutoff=end<request.KnowledgeCutoff?end:request.KnowledgeCutoff;
                var window=ExitAlertEvaluator.Windows(input,date,frozen.Rules);windows[(input.Key,date)]=window;
                if(window.Pct5.HasValue)valid5++;if(window.Pct20.HasValue)valid20++;
                if(!days.TryGetValue(date,out var findings))days[date]=findings=[];
                var single=ExitAlertEvaluator.Single(input,date,frozen.Rules,false);
                if(frozen.Rules.Enabled&&single.Triggered)findings.Add(single);
                // 先裁剪可知披露，再取最新共同基准；不允许次日公告或次日首见信息穿越。
                var available=frozen.Holdings.Where(h=>h.Market==input.Key.Market&&h.Ticker==input.Key.Ticker
                    &&h.PublishedAt<=cutoff&&h.FirstSeenAt<=cutoff&&h.HoldingAsOf<=date).ToArray();
                if(available.Length==0)continue;
                var baseline=available.Max(h=>h.HoldingAsOf);var holdings=available.Where(h=>h.HoldingAsOf==baseline).ToArray();
                var proof=ExitAlertEvaluator.Minimum(holdings,input,date,cutoff);
                if(frozen.Rules.Enabled&&proof.LowerBound>0)findings.Add(new("MinimumReduction",input.Key.Market+":"+input.Key.Ticker,
                    AlertLane.InstitutionEvidence,AlertSeverity.Watch,AlertEvidenceKind.DerivedMinimumReduction,date,
                    "机构持仓最低净减少 · 研究回放",proof.Explanation,
                    ExitAlertEvaluator.Hash(string.Join('|',proof.EvidenceIds)+"|"+proof.LowerBound),proof.EvidenceIds,true,true,true,
                    proof.LowerBound,[ExitAlertEvaluator.Snapshot(input,date)],holdings));
            }
            var accepted=dates.Count(d=>input.Points.Any(p=>p.Date==d&&p.Shares>0));
            coverage.Add(new(input.Key,dates.Length,accepted,valid5,valid20,
                input.CalendarVerified&&dates.Length>0?100m*accepted/dates.Length:null,input.CalendarVerified));
        }
        foreach(var (date,findings) in days)
        {
            var members=frozen.Members.Where(m=>m.ActiveFrom<=date&&m.ActiveThrough>=date).ToArray();
            // 某成员当日没有日历也不能从集群分母中消失；以缺失窗口阻断同步收缩结论。
            var inputs=members.Select(m=>(m.Input,windows.TryGetValue((m.Input.Key,date),out var value)?value:
                new AlertWindows(date,"IncompleteCalendar",null,null,null,[],"",false))).ToArray();
            foreach(var window in new[]{5,20})
            {
                var cluster=ExitAlertEvaluator.Cluster(inputs,date,frozen.Rules,false,window);
                if(frozen.Rules.Enabled&&cluster.Triggered)findings.Add(cluster);
            }
        }
        var rows=days.Select(x=>new AlertReplayDay(x.Key,x.Value)).ToArray();
        var first=rows.SelectMany(d=>d.Findings).GroupBy(f=>new{f.RuleId,f.ScopeId,f.Severity})
            .Select(g=>new AlertReplayFirst(g.Key.RuleId,g.Key.ScopeId,g.Key.Severity,g.Min(f=>f.ObservationDate))).ToArray();
        return new("RetrospectiveStudy","NotProven",frozen.Rules.Version,ExitAlertEvaluator.Hash(JsonSerializer.Serialize(frozen)),
            "仅为修订后历史研究，首次日期是模拟触发日。份额历史发布时间、当时采集许可、历史样本及规则可用性未获完整证明；不能证明当时已通知。覆盖率仅针对输入的已核验日历。无标签不计算误报/漏检，无承接行情不生成Critical，无NAV不计算金额。",
            coverage,first,rows,frozen);
    }
}
