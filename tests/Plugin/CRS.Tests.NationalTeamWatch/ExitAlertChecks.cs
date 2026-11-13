using System.Text;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>纯规则与普通SQLite隔离验收；合成原件、模拟时间和进程重建均不冒充真实长期采集或实际送达。</summary>
internal static class ExitAlertChecks
{
    private static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 预警："+label);}
    private static void Reject(Action action,string label){try{action();}catch(InvalidDataException){Check(true,label);return;}throw new Exception(label);}
    private static readonly EtfKey Key=new("SSE","510300");
    private static readonly DateOnly Date=new(2026,9,1);
    private const string Url="https://www.sse.com.cn/fixture-etf-alert.pdf";
    private static readonly string Hash=new('a',64);
    public static void Run(string root)
    {
        Domain();
        ExitAlertReadinessChecks.Run();
        Persistence(Path.Combine(root,"persistence"));
        Revisions(Path.Combine(root,"revisions"));
        ClearStreak(Path.Combine(root,"clear-streak"));
        CurrentDay(Path.Combine(root,"unfinished-day"));
    }
    private static void Domain()
    {
        var days=ReviewedExchangeCalendar.Parse("SSE",2026,DirectEquityChecks.CalendarHtml).Where(x=>x.Open&&x.Date<=Date).TakeLast(21).Select(x=>x.Date).ToArray();
        var metadata=new AlertFundMetadata("SSE","510300","fund-a","manager-a","CSI300",days[0],Date,Url,Hash,"synthetic-reviewer",true);
        var points=days.Select((d,i)=>new SharePoint(d,i==20?860:1000,null,"r"+i)).ToArray();
        var input=new AlertFundInput(Key,"合成ETF",days,points,metadata,true,new(2026,9,2,0,0,0,TimeSpan.FromHours(8)));
        var rules=new ExitAlertRules();
        var signal=ExitAlertEvaluator.Single(input,Date,rules,false);
        Check(signal.Triggered&&signal.Preview&&signal.EvidenceKind==AlertEvidenceKind.OfficialMarketObserved&&signal.Severity==AlertSeverity.Watch,"ALERT-01/20 份额收缩仅为市场观察，不升级机构身份、不输出金额");
        var missing=ExitAlertEvaluator.Windows(input with{Points=points.Where(x=>x.Date!=days[^2]).ToArray()},Date,rules);
        Check(missing.Pct5 is null&&missing.Pct20 is null&&!missing.Triggered,"ALERT-04 缺失相邻交易日不跨越、不填零");
        var partial=ExitAlertEvaluator.Windows(input with{Points=points.Skip(14).ToArray()},Date,rules);
        Check(partial.Pct5.HasValue&&partial.Pct20 is null&&partial.Triggered,"5日完整与20日不足独立显示");
        Check(!ExitAlertEvaluator.Single(input with{Metadata=metadata with{OriginalConfirmed=false}},Date,rules,true).Triggered,"ALERT-05 未核验折算可比性不生成正式流出信号");
        Check(!ExitAlertEvaluator.Single(input with{CalendarVerified=false},Date,rules,true).Triggered,"未经核验日历不能以周一至周五猜测窗口");
        var peers=Enumerable.Range(0,3).Select(i=>input with{Key=new("SSE","51030"+i),Metadata=metadata with{Ticker="51030"+i,FundId="fund-"+i,ManagerId="manager-"+i}}).ToArray();
        var cluster=ExitAlertEvaluator.Cluster(peers.Select(x=>(x,ExitAlertEvaluator.Windows(x,Date,rules))).ToArray(),Date,rules,false);
        Check(cluster.Triggered&&cluster.Severity==AlertSeverity.Warning&&!cluster.Explanation.Contains("人民币"),"ALERT-03 同一窗口三基金两管理人只生成样本池警告，不相加份额");
        var duplicate=peers.Select(x=>x with{Metadata=x.Metadata! with{FundId="one-fund"}}).Select(x=>(x,ExitAlertEvaluator.Windows(x,Date,rules))).ToArray();
        Check(!ExitAlertEvaluator.Cluster(duplicate,Date,rules,true).Triggered,"同一基金不同类别不重复凑成三只基金");
        var wrongDate=peers.Select((x,i)=>(x,ExitAlertEvaluator.Windows(x,Date,rules) with{Date=i==0?Date.AddDays(-1):Date})).ToArray();
        Check(!ExitAlertEvaluator.Cluster(wrongDate,Date,rules,true).Triggered,"不同观察日期不能组成同步收缩集群");
        var mild=input with{Points=days.Select((d,i)=>new SharePoint(d,i==20?990.1m:1000,null,"m"+i)).ToArray()};
        Check(!ExitAlertEvaluator.Single(mild,Date,rules,true).Triggered,"ALERT-15 约负0.99%的样本不宣布大规模退出");
        var holding=new NamedEtfHolding("h1","SSE","510300","legal-a","合成具名法人","account-a",days[0],
            new(2026,8,28,18,0,0,TimeSpan.FromHours(8)),new(2026,8,29,0,0,0,TimeSpan.FromHours(8)),950,Url,Hash,"P10 股东表","reviewer",true,true);
        var cutoff=new DateTimeOffset(2026,9,2,0,0,0,TimeSpan.FromHours(8));
        Check(ExitAlertEvaluator.Minimum([holding],input,Date,cutoff).LowerBound==90,"ALERT-06 具名同口径基准950减全市场860，下界90，不称单日成交");
        Check(ExitAlertEvaluator.Minimum([holding],input with{CalendarVerified=false},Date,cutoff).LowerBound is null
            &&ExitAlertEvaluator.Minimum([holding],input with{TradingDays=days[..^1]},Date,cutoff).LowerBound is null,
            "未核验日历或非交易日的当前份额不能作为机构减持下界");
        Check(ExitAlertEvaluator.Minimum([holding],input with{Points=points.Select(p=>p.Date==Date?p with{Shares=0}:p).ToArray()},Date,cutoff).LowerBound is null
            &&ExitAlertEvaluator.Minimum([holding],input with{Points=points.Select(p=>p.Date==Date?p with{RevisionId=""}:p).ToArray()},Date,cutoff).LowerBound is null,
            "零份额和无有效修订ID的观测不形成具名机构减持证明");
        Check(ExitAlertEvaluator.Minimum([holding with{PublishedAt=cutoff.AddDays(1)}],input,Date,cutoff).LowerBound is null
            &&ExitAlertEvaluator.Minimum([holding with{FirstSeenAt=cutoff.AddDays(1)}],input,Date,cutoff).LowerBound is null,"ALERT-07 公布或首见在截止时点之后的证据不回看使用");
        Check(ExitAlertEvaluator.Minimum([holding with{LegalName="机构1"}],input,Date,cutoff).LowerBound is null,"ALERT-08 匿名机构不按份额推断汇金身份");
        Check(ExitAlertEvaluator.Minimum([holding,holding],input,Date,cutoff).LowerBound is null
            &&ExitAlertEvaluator.Minimum([holding with{IndependentOwnershipConfirmed=false}],input,Date,cutoff).LowerBound is null,"重叠持仓或独立性未确认不能相加计算下界");
        Check(!ExitAlertEvaluator.Single(input with{Metadata=metadata with{Ticker="159919"}},Date,rules,true).Triggered
            &&ExitAlertEvaluator.Minimum([holding with{PublishedAt=new(2026,1,1,0,0,0,TimeSpan.Zero)}],input,Date,cutoff).LowerBound is null,"其它证券的可比性或早于报告基准的公布时点不能产生证明");
    }
    internal static MarketRepository Setup(string root,ManualClock clock,out RawEvidenceStore raw)
    {
        raw=new(Path.Combine(root,"raw"));var repository=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);
        foreach(var item in repository.Configuration().Where(x=>x.Market!=Key.Market||x.Ticker!=Key.Ticker))repository.SetMembership(new(item.Market,item.Ticker),item.Name,item.IndexGroup,false,"synthetic alert fixture");
        var config=repository.AlertConfiguration();repository.SaveAlertConfiguration(config with{Rules=config.Rules with{Version="exit-fixture.1",NotifyWatch=true}});
        repository.RegisterReviewedCalendar("SSE",2026,Encoding.UTF8.GetBytes(DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source("SSE",2026));
        repository.RegisterAlertDocument(new(Key.Market,Key.Ticker,Url),"%PDF-synthetic-etf-alert"u8.ToArray());
        Metadata(repository,clock);
        var days=ReviewedExchangeCalendar.Parse("SSE",2026,DirectEquityChecks.CalendarHtml).Where(x=>x.Open&&x.Date<=Date).TakeLast(21).Select(x=>x.Date).ToArray();
        Import(repository,raw,days.Select((d,i)=>(d,i==20?860m:1000m)).ToArray());
        return repository;
    }
    private static void Metadata(MarketRepository repository,ManualClock clock)=>repository.ReviewAlertMetadata(new(repository.AlertDocuments(Key).Single().Id,"fund-a","manager-a","CSI300",new(2026,1,1),DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),"synthetic-reviewer",true));
    private static void Import(MarketRepository repository,RawEvidenceStore raw,params (DateOnly Date,decimal Shares)[] points)
    {
        var text="market,ticker,trade_date,total_shares,unit\n"+string.Join('\n',points.Select(x=>$"SSE,510300,{x.Date:yyyy-MM-dd},{x.Shares.ToString(System.Globalization.CultureInfo.InvariantCulture)},份"));
        var fetch=repository.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(text)),Url);
        repository.ImportShares(fetch,CRS.Plugins.NationalTeamWatch.Application.ImportFormat.Shares(text),default);
    }
    private static SqliteConnection OpenDatabase(string root)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"data","market.db"),Pooling=false}.ToString());connection.Open();return connection;
    }
    private static void Sql(SqliteConnection connection,string sql){using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void Persistence(string root)
    {
        var clock=new ManualClock();string id;string second;
        using(var repository=Setup(root,clock,out var raw))
        {
            using(var connection=OpenDatabase(root))
            {
                Sql(connection,"CREATE TRIGGER synthetic_outbox_abort BEFORE INSERT ON nt_alert_outbox BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;");
                try{repository.EvaluateExitAlerts(true);throw new Exception("注入故障未回滚");}catch(SqliteException){}
                Check(repository.AlertEvents(new()).Count==0&&repository.AlertOutbox().Count==0,"事件与Outbox插入失败同事务回滚，不产生孤立事件");
                using(var journal=connection.CreateCommand())
                {
                    journal.CommandText="SELECT (SELECT count(*) FROM nt_risk_event_change)+(SELECT count(*) FROM nt_alert_input_snapshot)+(SELECT count(*) FROM nt_alert_notification_input)";
                    Check(Convert.ToInt32(journal.ExecuteScalar())==0,"模拟Outbox落库失败时变更日志和冻结证据也必须整体回滚");
                }
                Sql(connection,"DROP TRIGGER synthetic_outbox_abort;");
            }
            repository.EvaluateExitAlerts(true);var risk=repository.AlertEvents(new("EtfOutflow")).Single();
            Check(risk.Preview&&repository.AlertOutbox().Single().State=="BlockedAcceptance","M0未通过只存研究预览，金融通知禁止外发");
            repository.EvaluateExitAlerts(true);
            Check(repository.AlertChanges(risk.Id).Count==1&&repository.AlertOutbox().Count==1,"重复评价相同输入不重复事件或待发通知");
            Reject(()=>repository.RetryAlertNotification(repository.AlertOutbox().Single().Id),"人工重试不能绕过正式数据门槛");
            var attempt=repository.BeginCollection(Key,Date);repository.RecordCollection(attempt,"AccessBlocked","Http403");
            repository.EvaluateExitAlerts(true);var quality=repository.AlertEvents(new("DataQuality")).Single();
            Check(quality.State=="PendingVerification"&&!quality.Preview,"ALERT-12 来源403独立数据核查车道，不归因机构卖出");
            var claim=repository.ClaimAlertNotification()!;id=claim.Id;
            Check(claim.State=="InFlight"&&claim.ChannelId=="email","先持久领取指定渠道，再开始提交");
            // 模拟提交前进程退出；重建仓储保留实际状态，不假称已发送。
        }
        using(var reopened=TestMarketData.Open(Path.Combine(root,"data"),new(Path.Combine(root,"raw")),timeProvider:clock))
        {
            Check(reopened.AlertOutbox().Single(x=>x.Id==id).State=="OutcomeUnknown"&&reopened.ClaimAlertNotification() is null,"ALERT-10 重启后在途记录保留受理未知，不自动重复外发");
            Reject(()=>reopened.RetryAlertNotification(id),"未知受理结果不能从安全重试入口再次发送");
            var config=reopened.AlertConfiguration();reopened.SaveAlertConfiguration(config with{Rules=config.Rules with{Version="exit-v2.0"},Routes=[new("dingtalk","research")]});
            reopened.EvaluateExitAlerts(true);var claim=reopened.ClaimAlertNotification()!;second=claim.Id;
            Check(claim.ChannelId=="dingtalk"&&claim.ProfileId=="research","切换渠道和Profile无需修改风险规则算法");
            // 投递审计写失败时，Outbox状态不能单独提交为“已失败/可重试”。
            using(var connection=OpenDatabase(root))
            {
                Sql(connection,"CREATE TRIGGER synthetic_delivery_abort BEFORE INSERT ON nt_alert_delivery BEGIN SELECT RAISE(ABORT,'synthetic delivery failure'); END;");
                try{reopened.CompleteAlertNotification(claim.Id,claim.Lease!,new("RetryableFailure","Http429"));throw new Exception("投递审计故障未回滚");}
                catch(SqliteException){}
                Check(reopened.AlertOutbox().Single(x=>x.Id==second).State=="InFlight"&&reopened.AlertDeliveries(second).Count==0,
                    "投递审计失败时租约与Outbox状态均回滚，不单独宣称通知失败");
                Sql(connection,"DROP TRIGGER synthetic_delivery_abort;");
            }
            reopened.CompleteAlertNotification(claim.Id,claim.Lease!,new("RetryableFailure","Http429"));
            Check(reopened.AlertOutbox().Single(x=>x.Id==second).State=="Retry"&&reopened.AlertDeliveries(second).Count==1,"ALERT-14 明确未受理的429保留失败码和退避，不记送达");
            reopened.RetryAlertNotification(second);claim=reopened.ClaimAlertNotification()!;
            reopened.CompleteAlertNotification(claim.Id,claim.Lease!,new("Accepted","ProviderAccepted","synthetic-receipt"));
            Check(reopened.AlertOutbox().Single(x=>x.Id==second).StatusText=="渠道已受理，送达未验证"&&reopened.AlertDeliveries(second).Count==2,"渠道受理和提供商凭证保留，不能声称用户收到");
            Reject(()=>reopened.RetryAlertNotification(second),"已受理通知不能安全重发");
            Check(reopened.AlertEvents(new(State:"RuleSuperseded")).Count==2,"规则替换保留旧事件，不能伪装市场风险解除");
            Reject(()=>reopened.SaveAlertConfiguration(config),"旧配置修订不能覆盖新的规则与订阅");
        }
    }
    private static void Revisions(string root)
    {
        var clock=new ManualClock();using var repository=Setup(root,clock,out var raw);repository.EvaluateExitAlerts(true);
        var original=repository.AlertEvents(new("EtfOutflow")).Single();Import(repository,raw,(Date,1000));
        repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {DataHealth:"Unknown",State:"Active"},"ALERT-02 待审修订触发数据核查，未知输入不能解除既有风险");
        var revision=repository.Evidence(Key).Single(x=>x.State=="NeedsReview");repository.Review(revision.RevisionId,true,"synthetic-reviewer",true);
        repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {State:"RetractedByRevision"}&&repository.AlertChanges(original.Id).Last().Kind=="RetractedByRevision","ALERT-09 接受修订后重算并撤销原信号，保留更正历史");
        Check(repository.AlertEvent(original.Id,1)!.State=="Active"&&repository.AlertInputs(original.Id,1).Any(x=>x.RevisionId!=revision.RevisionId&&x.TradeDate==Date.ToString("yyyy-MM-dd")),"历史事件版本仍定位原始输入，不用新修订覆盖旧证据");
    }
    private static void ClearStreak(string root)
    {
        var clock=new ManualClock();using var repository=Setup(root,clock,out var raw);repository.EvaluateExitAlerts(true);
        var original=repository.AlertEvents(new("EtfOutflow")).Single();
        clock.Value=new(2026,9,3,0,0,0,TimeSpan.Zero);Metadata(repository,clock);Import(repository,raw,(new(2026,9,2),1000));repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {State:"Active",ClearCount:1},"首次正常窗口保留关注，不能立刻解除");
        clock.Value=new(2026,9,7,0,0,0,TimeSpan.Zero);Metadata(repository,clock);Import(repository,raw,(new(2026,9,3),1000),(new(2026,9,4),1000));repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {State:"Active",ClearCount:1},"ALERT-11 停机跨交易日不能累计成连续解除观察");
        clock.Value=new(2026,9,8,0,0,0,TimeSpan.Zero);Metadata(repository,clock);Import(repository,raw,(new(2026,9,7),1000));repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {State:"Resolved",ClearCount:2},"相邻真实交易日的完整正常窗口才解除风险");
        var resolved=repository.AlertEvent(original.Id)!;var resolution=repository.AlertOutbox().Single(x=>x.EventId==resolved.Id&&x.ChangeNo==resolved.ChangeNo);
        using(var connection=OpenDatabase(root))
        {
            using var proof=connection.CreateCommand();proof.CommandText="SELECT payload FROM nt_alert_notification_input WHERE event_id=$id AND sequence=$n";
            proof.Parameters.AddWithValue("$id",resolved.Id);proof.Parameters.AddWithValue("$n",resolved.ChangeNo);
            var inputs=System.Text.Json.JsonSerializer.Deserialize<AlertFundInput[]>((string)proof.ExecuteScalar()!)!;
            Check(inputs.Single().Points.Any(x=>x.Date==new DateOnly(2026,9,7))&&inputs.Single().EvaluatedAt==clock.Value,"解除通知冻结当次正常窗口，不能借用原触发窗口");
            var pending=System.Text.Json.JsonSerializer.Serialize(resolution with{State="Pending"}).Replace("'","''",StringComparison.Ordinal);
            Sql(connection,"UPDATE nt_alert_outbox SET state='Pending',payload='"+pending+"' WHERE id='"+resolution.Id+"'");
        }
        Check(repository.ClaimAlertNotification() is null&&repository.AlertOutbox().Single(x=>x.Id==resolution.Id).LastCode=="DataAcceptanceNotPassed",
            "解除通知的本次原件门槛通过后，仍保留原正式数据门槛");
        Import(repository,raw,(Date,1000));repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id) is {DataHealth:"Unknown"},"已解除历史事件遇到待审修订也保留未知状态，不继续冒充证据完整");
        var revised=repository.Evidence(Key).Single(x=>x.State=="NeedsReview");repository.Review(revised.RevisionId,true,"synthetic-reviewer",true);repository.EvaluateExitAlerts(true);
        var corrected=repository.AlertEvent(original.Id)!;var changes=repository.AlertChanges(original.Id).Count;
        Check(corrected.State=="RetractedByRevision"&&corrected.DataHealth=="Complete"&&corrected.FirstSeenAt==original.FirstSeenAt&&corrected.HistoricalRevisionNote!.Contains("修订后视角"),"ALERT-09 已关闭事件按冻结成员、原规则及原时点口径回算，撤销全部失效窗口且不改写首见");
        Check(repository.AlertInputs(original.Id).Any(x=>x.RevisionId==revised.RevisionId),"历史更正详情同时定位新修订输入与未覆盖的原版本");
        repository.EvaluateExitAlerts(true);
        Check(repository.AlertChanges(original.Id).Count==changes,"历史回算指纹持久化，不在每次评价重复更正");
    }
    internal sealed class ManualClock:TimeProvider
    {
        public DateTimeOffset Value=new(2026,9,2,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Value;
    }
    private static void CurrentDay(string root)
    {
        var clock=new ManualClock();using var repository=Setup(root,clock,out var raw);repository.EvaluateExitAlerts(true);
        var original=repository.AlertEvents(new("EtfOutflow")).Single();Import(repository,raw,(new(2026,9,2),700));repository.EvaluateExitAlerts(true);
        Check(repository.AlertEvent(original.Id)!.LastTradeDate==Date,"当日尚未结束的份额不能冒充新的日终风险信号");
    }
}
