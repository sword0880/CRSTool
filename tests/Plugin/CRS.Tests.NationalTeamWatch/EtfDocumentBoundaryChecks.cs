using System.Globalization;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>真实普通SQLite、正式仓储及合成原件；验证旧确认不能越过当前来源/Q15门槛，不发送通知。</summary>
internal static class EtfDocumentBoundaryChecks
{
    private static readonly EtfKey Key=new("SSE","510300");
    private sealed class Clock:TimeProvider
    {public DateTimeOffset At=new(2026,9,2,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>At;}
    private static int passed;
    public static void Run(string root)
    {
        passed=0;var clock=new Clock();var reads=new System.Collections.Concurrent.ConcurrentBag<string>();
        var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath,openRead:p=>{reads.Add(Path.GetFileNameWithoutExtension(p));return File.OpenRead(p);});
        var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,raw,timeProvider:clock);
        foreach(var fund in repo.Configuration().Where(x=>x.Ticker!=Key.Ticker))repo.SetMembership(new(fund.Market,fund.Ticker),fund.Name,fund.IndexGroup,false,"synthetic disclosure boundary");
        repo.RegisterReviewedCalendar("SSE",2026,Encoding.UTF8.GetBytes(DirectEquityChecks.CalendarHtml),ReviewedExchangeCalendar.Source("SSE",2026));
        var day=new DateOnly(2026,9,1);var days=ReviewedExchangeCalendar.Parse("SSE",2026,DirectEquityChecks.CalendarHtml).Where(x=>x.Open&&x.Date<=day).TakeLast(21).Select(x=>x.Date).ToArray();
        var shares="market,ticker,trade_date,total_shares,unit\n"+string.Join('\n',days.Select((d,i)=>$"SSE,510300,{d:yyyy-MM-dd},{(i==20?860:1000)},份"));
        var shareFetch=repo.RecordFetch(raw.Save(Encoding.UTF8.GetBytes(shares)),"https://www.sse.com.cn/document-boundary-shares");repo.ImportShares(shareFetch,ImportFormat.Shares(shares),default);
        const string url="https://www.sse.com.cn/document-boundary.pdf";var original="%PDF-synthetic-disclosure-boundary"u8.ToArray();
        repo.RegisterAlertDocument(new(Key.Market,Key.Ticker,url),original);var document=repo.AlertDocuments(Key).Single();
        var metadataReview=new AlertMetadataReview(document.Id,"fund-a","manager-a","CSI300",new(2026,1,1),day,"synthetic-reviewer",true);
        repo.ReviewAlertMetadata(metadataReview);
        var holdingReview=new NamedEtfHoldingReview(document.Id,"synthetic-legal-a","合成具名法人甲","合成独立账户甲",new(2026,8,1),new(2026,8,28,18,0,0,TimeSpan.FromHours(8)),950,"合成第10页","synthetic-reviewer",true,true);
        repo.ReviewNamedEtfHolding(holdingReview);
        Check(repo.NamedEtfHoldings(Key).Single().OriginalConfirmed&&repo.AlertMetadata().Single().OriginalConfirmed,"完整来源登记及实际首见身份可保持既有人工确认");
        reads.Clear();_ =repo.AlertMetadata();Check(reads.Count(x=>x==document.Hash)==1,"同次批量核验相同原件只读取一次");
        var configuration=repo.AlertConfiguration();repo.SaveAlertConfiguration(configuration with{Rules=configuration.Rules with{Version="document-boundary-v1",NotifyWatch=true}});repo.EvaluateExitAlerts(true);
        var minimum=repo.AlertEvents(new("InstitutionEvidence")).Single();Check(minimum.Explanation.Contains("下界90")&&minimum.DataHealth=="Complete","经真实仓储的具名持仓基准生成合成下界90，保留研究预览门槛");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());db.Open();
        void Sql(string text){using var command=db.CreateCommand();command.CommandText=text;command.ExecuteNonQuery();}
        string? Scalar(string text){using var command=db.CreateCommand();command.CommandText=text;return command.ExecuteScalar() as string;}
        long Changes(){using var command=db.CreateCommand();command.CommandText="SELECT total_changes()";return Convert.ToInt64(command.ExecuteScalar());}
        var frozen=Scalar("SELECT holdings FROM nt_alert_input_snapshot WHERE event_id='"+minimum.Id+"'");
        var fetch=Scalar("SELECT fetch_id FROM nt_alert_document WHERE id='"+document.Id+"'")!;
        // 只把隔离的研究待发记录置回Pending，单独验证原件领取闸门；没有授予正式数据门槛或真实通知能力。
        var notification=repo.AlertOutbox().Single(x=>x.EventId==minimum.Id);
        Sql("UPDATE nt_alert_outbox SET state='Pending',payload='"+JsonSerializer.Serialize(notification with{State="Pending"}).Replace("'","''",StringComparison.Ordinal)+"' WHERE id='"+notification.Id+"'");
        Sql($"INSERT INTO quality_issue VALUES('synthetic-proof-q15','Q15','fixture','{fetch}','Critical','Open','{fetch}',NULL,NULL)");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"原件字节完整但Q15未解除时，既有两类确认均失效");
        Reject(()=>repo.ReviewAlertMetadata(metadataReview),"未解除Q15不能再次确认可比性");
        Reject(()=>repo.ReviewNamedEtfHolding(holdingReview with{Account="合成独立账户乙"}),"未解除Q15不能确认新持仓账户");
        Reject(()=>repo.EtfHolderDocument(Key,document.Id),"候选读取沿用相同Q15门槛");
        Check(repo.ClaimAlertNotification() is null&&repo.AlertOutbox().Single(x=>x.Id==notification.Id) is{LastCode:"DisclosureEvidenceUnavailable",StatusText:"原件待重新核验，禁止外发"},
            "领取前重验本条金融通知冻结原件，失败不领取租约");
        repo.EvaluateExitAlerts(true);
        Check(repo.AlertEvent(minimum.Id) is{DataHealth:"Unknown",State:"Active"},"当前证据不足不解除已有金融观察");
        Check(Scalar("SELECT rechecked FROM nt_alert_input_snapshot WHERE event_id='"+minimum.Id+"'") is not null
            &&Scalar("SELECT holdings FROM nt_alert_input_snapshot WHERE event_id='"+minimum.Id+"'")==frozen,"历史原快照按统一门槛回算，但不改写原冻结持仓");
        Check(repo.AlertEvents(new("DataQuality")).Any(x=>x.RuleId=="DataQualityAnomaly"),"原件隔离另入数据核查车道");
        Sql("UPDATE quality_issue SET status='Resolved' WHERE id='synthetic-proof-q15'");
        Check(repo.NamedEtfHoldings(Key).Single().OriginalConfirmed&&repo.AlertMetadata().Single().OriginalConfirmed,"明确解除隔离后可重新核验原确认，读取不写审核");
        Sql("UPDATE source_fetch SET source_url='https://www.sse.com.cn/other.pdf' WHERE fetch_id='"+fetch+"'");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"来源URL串件即使哈希相同也不能继承旧确认");
        Reject(()=>repo.ReviewAlertMetadata(metadataReview),"来源身份串件不能重新批准");
        Sql("UPDATE source_fetch SET source_url='"+url+"',status='ParseFailed' WHERE fetch_id='"+fetch+"'");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"已解析失败的来源不参与已核金融输入");
        Sql("UPDATE source_fetch SET status='RawSaved',parser_version='unknown' WHERE fetch_id='"+fetch+"'");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"未知来源记录版本不按当前人工原件合同解释");
        Sql("UPDATE source_fetch SET parser_version='human-etf-proof-v1' WHERE fetch_id='"+fetch+"'");
        var retrieved=Scalar("SELECT retrieved_utc FROM source_fetch WHERE fetch_id='"+fetch+"'")!;
        Sql("UPDATE source_fetch SET retrieved_utc='"+clock.At.AddDays(1).ToString("O",CultureInfo.InvariantCulture)+"' WHERE fetch_id='"+fetch+"'");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"原件首见早于来源取得时刻不能构造提前知识");
        Sql("UPDATE source_fetch SET retrieved_utc='2026-09-02' WHERE fetch_id='"+fetch+"'");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed,"来源时刻缺少完整时区不能补造精确取得时刻");
        Sql("UPDATE source_fetch SET retrieved_utc='"+retrieved+"' WHERE fetch_id='"+fetch+"'");
        var holding=repo.NamedEtfHoldings(Key).Single();var changed=holding with{FirstSeenAt=holding.FirstSeenAt.AddDays(-1)};
        Sql("UPDATE nt_named_etf_holding SET payload='"+JsonSerializer.Serialize(changed).Replace("'","''",StringComparison.Ordinal)+"'");
        Check(!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"持仓首见与原件登记不符不能提前进入知识视图");
        Sql("UPDATE nt_named_etf_holding SET payload='"+JsonSerializer.Serialize(holding).Replace("'","''",StringComparison.Ordinal)+"'");
        var before=Changes();
        var healthy=((IMarketReadSessions)repo).Read(r=>r.NamedEtfHoldings(Key).Single().OriginalConfirmed&&r.AlertMetadata().Single().OriginalConfirmed,default);
        Check(healthy&&Changes()==before,"独立只读会话核验完整证据且不写入恢复或审核状态");
        var other=new EtfKey("SSE","510050");repo.RegisterAlertDocument(new(other.Market,other.Ticker,"https://www.sse.com.cn/unrelated-proof.pdf"),"%PDF-unrelated-proof"u8.ToArray());
        var unrelated=repo.AlertDocuments(other).Single();
        using(var locked=new FileStream(Path.Combine(rawPath,unrelated.Hash+".raw"),FileMode.Open,FileAccess.Read,FileShare.None))
            Check(repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"逐基金持仓不读取锁定的无关原件");
        File.WriteAllText(Path.Combine(rawPath,document.Hash+".raw"),"damaged");
        Check(!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed&&!repo.AlertMetadata().Single().OriginalConfirmed,"新损坏立即撤去金融输入确认");
        File.WriteAllBytes(Path.Combine(rawPath,document.Hash+".raw"),original);
        Check(repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"同哈希恢复且无隔离记录时，下次读取重新核验，不跨查询缓存");
        using(var canceled=new CancellationTokenSource())
        {canceled.Cancel();try{repo.ExecuteOperation(()=>repo.AlertMetadata(),canceled.Token);throw new Exception("忽略取消");}catch(OperationCanceledException){Check(true,"证据核验保留查询取消语义");}}
        Sql("UPDATE feature_schema_migration SET version=99 WHERE feature='alert-notification-proof'");
        Reject(()=>{using var invalid=TestMarketData.Open(data,raw,timeProvider:clock);},"未知通知证据结构版本拒绝启动");
        Sql("UPDATE feature_schema_migration SET version=1 WHERE feature='alert-notification-proof'");
        Sql("ALTER TABLE nt_alert_notification_input RENAME TO synthetic_notification_backup");
        Reject(()=>{using var invalid=TestMarketData.Open(data,raw,timeProvider:clock);},"已有迁移登记却缺少证据表时不静默重建为空");
        Sql("ALTER TABLE synthetic_notification_backup RENAME TO nt_alert_notification_input");
        Batch(Path.Combine(root,"batch"));
        Console.WriteLine("ETF原件边界通过："+passed+"项；普通SQLite隔离库，不代表真实持仓或已安装服务验收。");
    }
    private static void Batch(string root)
    {
        var reads=new System.Collections.Concurrent.ConcurrentBag<string>();var clock=new Clock();var rawPath=Path.Combine(root,"raw");
        var raw=new RawEvidenceStore(rawPath,openRead:p=>{reads.Add(Path.GetFileNameWithoutExtension(p));return File.OpenRead(p);});
        var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,raw,timeProvider:clock);
        string? firstDocument=null;
        for(var i=0;i<129;i++)
        {
            var url=$"https://www.sse.com.cn/batch-proof-{i}.pdf";repo.RegisterAlertDocument(new(Key.Market,Key.Ticker,url),"%PDF-shared-batch-proof"u8.ToArray());
            var document=repo.AlertDocuments(Key).Single(x=>x.Url==url);firstDocument??=document.Id;
            repo.ReviewNamedEtfHolding(new(document.Id,"synthetic-batch-"+i,"合成具名法人甲","合成独立账户"+i,new(2026,8,1),new(2026,8,28,18,0,0,TimeSpan.FromHours(8)),1,"合成第1页","synthetic-batch-reviewer",true,true));
        }
        reads.Clear();var rows=repo.NamedEtfHoldings(Key);
        Check(rows.Count==129&&rows.All(x=>x.OriginalConfirmed)&&reads.Count==1,"跨128条分组读取不丢披露，同哈希仅实际读取一次");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"market.db"),Pooling=false}.ToString());db.Open();using var command=db.CreateCommand();
        command.CommandText="UPDATE source_fetch SET source_url='https://www.sse.com.cn/wrong.pdf' WHERE fetch_id=(SELECT fetch_id FROM nt_alert_document WHERE id=$id)";command.Parameters.AddWithValue("$id",firstDocument!);command.ExecuteNonQuery();
        rows=repo.NamedEtfHoldings(Key);
        Check(rows.Count(x=>!x.OriginalConfirmed)==1&&rows.Count(x=>x.OriginalConfirmed)==128,"相同原件字节的不同来源独立验证，不用一个健康来源覆盖坏来源");
    }
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);passed++;Console.WriteLine("PASS ETF原件边界："+label);}
    private static void Reject(Action work,string label){try{work();}catch(InvalidDataException){Check(true,label);return;}throw new Exception("未拒绝："+label);}
}
