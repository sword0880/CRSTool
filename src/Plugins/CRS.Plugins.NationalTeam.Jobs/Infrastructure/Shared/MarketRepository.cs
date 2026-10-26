using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>公开研究数据仓储；追加修订并保留来源，不接触私人税务库。</summary>
public sealed partial class MarketRepository : IMarketRepository,IEtfDashboardRepository,IEvidenceIntegrityRepository
{
    private readonly SqliteConnection db;
    private readonly RawEvidenceStore raw;
    private readonly Action? beforeWrite;
    private readonly TimeProvider clock;
    private CancellationToken operationCancellation;
    private readonly IMarketRepositorySession marketSession;
    private readonly IndustryPeStore industryPeStore;
    private readonly MarketCalendarStore marketCalendarStore;

    private sealed class MarketRepositorySession : IMarketRepositorySession
    {
        private readonly MarketRepository owner;
        public MarketRepositorySession(MarketRepository owner)=>this.owner=owner;
        public SqliteConnection Connection=>owner.db;
        public RawEvidenceStore RawEvidence=>owner.raw;
        public Action? BeforeWrite=>owner.beforeWrite;
        public TimeProvider Clock=>owner.clock;
        public CancellationToken OperationCancellation=>owner.operationCancellation;
        public SqliteCommand Command(string sql,params (string,object?)[] args)=>owner.Command(sql,args);
        public void Execute(string sql,params (string,object?)[] args)=>owner.Execute(sql,args);
        public object? Scalar(string sql,params (string,object?)[] args)=>owner.Scalar(sql,args);
        public void CheckReadLimit(int count)=>owner.CheckReadLimit(count);
        public void RecordCommand(Guid? id,string operation)=>owner.RecordCommand(id,operation);
    }

    /// <summary>应用用例已持有串行门槛，作用域结束恢复令牌，取消不能泄漏到下一次查询。</summary>
    public T ExecuteOperation<T>(Func<T> work,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var previous=operationCancellation;operationCancellation=token;
        // 哈希循环之外，SQLite执行期间也必须可中断；串行门槛保证不会取消另一个用例。
        using var interrupt=token.Register(()=>SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        try{token.ThrowIfCancellationRequested();var result=work();token.ThrowIfCancellationRequested();return result;}
        catch(SqliteException error) when(error.SqliteErrorCode==9&&token.IsCancellationRequested)
        {throw new OperationCanceledException("市场查询已取消。",error,token);}
        catch(SqliteException error){throw new IOException("插件市场仓储操作失败。",error);}
        finally{operationCancellation=previous;}
    }
    public MarketRepository(string dataPath, RawEvidenceStore rawStore,Action? beforeWrite=null,byte[]? encryptionKey=null,TimeProvider? timeProvider=null,IMarketCycleApprovalWriteAuthority? approvalWriteAuthority=null)
    {
        if(encryptionKey is not {Length:32})throw new InvalidDataException("市场库必须提供独立的32字节加密密钥。");
        marketCycleApprovalWriteAuthority=approvalWriteAuthority;
        this.beforeWrite=beforeWrite;
        clock=timeProvider??TimeProvider.System; // 生产固定系统时间，隔离测试可推进时钟验证停机及相邻交易日规则。
        raw = rawStore; Directory.CreateDirectory(dataPath);
        // 共用宿主锁定的原生提供器版本，但不共用税务连接或加密密钥。
        SQLitePCL.Batteries_V2.Init();
        db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataPath, "market.db"), Password=Convert.ToHexString(encryptionKey),Pooling = false }.ToString()); db.Open();
        marketSession=new MarketRepositorySession(this);
        industryPeStore=new IndustryPeStore(marketSession);
        marketCalendarStore=new MarketCalendarStore(marketSession);
        readConnectionString=new SqliteConnectionStringBuilder(db.ConnectionString){Mode=SqliteOpenMode.ReadOnly}.ToString();
        Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        // 测试阶段直接使用Worker专用格式，不把旧桌面库或旧测试格式当作当前数据。
        if (version is not (0 or 2)) { db.Dispose(); throw new InvalidDataException("市场研究库格式不兼容，请清理旧测试库后重新采集。"); }
        Execute("""
            CREATE TABLE IF NOT EXISTS plugin_schema_migration(schema_version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL, plugin_package_version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS worker_business_command(request TEXT PRIMARY KEY,operation TEXT NOT NULL,completed_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS worker_sync_cursor(id INTEGER PRIMARY KEY CHECK(id=1),checked_through TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS market_activity_cursor(id INTEGER PRIMARY KEY CHECK(id=1),checked_through TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS market_activity_attempt(market TEXT,trade_date TEXT,state TEXT NOT NULL,PRIMARY KEY(market,trade_date));
            CREATE TABLE IF NOT EXISTS market_activity_observation(market TEXT,trade_date TEXT,amount_yuan TEXT NOT NULL,volume_shares TEXT NOT NULL,fetch_id TEXT NOT NULL,PRIMARY KEY(market,trade_date,fetch_id),FOREIGN KEY(fetch_id) REFERENCES source_fetch(fetch_id));
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
            CREATE TABLE IF NOT EXISTS source_fetch(fetch_id TEXT PRIMARY KEY,provider TEXT NOT NULL,retrieved_utc TEXT NOT NULL,source_published_utc TEXT,source_url TEXT NOT NULL,sha256 TEXT NOT NULL,original_sha256 TEXT NOT NULL,status TEXT NOT NULL,parser_version TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS etf_collection_attempt(id TEXT PRIMARY KEY,market TEXT NOT NULL,ticker TEXT NOT NULL,trade_date TEXT NOT NULL,started_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_etf_collection_day ON etf_collection_attempt(market,ticker,trade_date);
            CREATE TABLE IF NOT EXISTS etf_collection_transition(sequence INTEGER PRIMARY KEY AUTOINCREMENT,attempt_id TEXT NOT NULL REFERENCES etf_collection_attempt(id),state TEXT NOT NULL,reason TEXT NOT NULL,fetch_id TEXT REFERENCES source_fetch(fetch_id),updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_etf_collection_transition ON etf_collection_transition(attempt_id,sequence);
            CREATE TABLE IF NOT EXISTS trading_day(market TEXT,trade_date TEXT,fetch_id TEXT NOT NULL,PRIMARY KEY(market,trade_date),FOREIGN KEY(fetch_id) REFERENCES source_fetch(fetch_id));
            CREATE TABLE IF NOT EXISTS share_observation_revision(revision_id TEXT PRIMARY KEY,market TEXT,ticker TEXT,trade_date TEXT,total_shares TEXT NOT NULL,original_value TEXT NOT NULL,original_unit TEXT NOT NULL,fetch_id TEXT NOT NULL,revision_no INTEGER NOT NULL,accepted_at TEXT,first_seen TEXT NOT NULL,status TEXT NOT NULL,supersedes_id TEXT,UNIQUE(market,ticker,trade_date,revision_no),FOREIGN KEY(fetch_id) REFERENCES source_fetch(fetch_id));
            CREATE INDEX IF NOT EXISTS ix_share_evidence_date ON share_observation_revision(trade_date DESC,revision_no DESC,revision_id);
            CREATE TABLE IF NOT EXISTS share_accepted_current(market TEXT,ticker TEXT,trade_date TEXT,revision_id TEXT NOT NULL,PRIMARY KEY(market,ticker,trade_date),FOREIGN KEY(revision_id) REFERENCES share_observation_revision(revision_id));
            CREATE TABLE IF NOT EXISTS quality_issue(id TEXT PRIMARY KEY,rule_id TEXT NOT NULL,rule_version TEXT NOT NULL,target_id TEXT NOT NULL,severity TEXT NOT NULL,status TEXT NOT NULL,evidence_id TEXT NOT NULL,reviewer TEXT,decision_time TEXT);
            CREATE INDEX IF NOT EXISTS ix_quality_evidence_rule_status ON quality_issue(evidence_id,rule_id,status);
            CREATE TABLE IF NOT EXISTS evidence_integrity_event(id TEXT PRIMARY KEY,fetch_id TEXT NOT NULL,event TEXT NOT NULL,checked_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_checkpoint(fetch_id TEXT PRIMARY KEY,status TEXT NOT NULL,accepted_count INTEGER NOT NULL);
            INSERT OR IGNORE INTO institution VALUES('huijin-investment','中央汇金投资有限责任公司'),('huijin-asset','中央汇金资产管理有限责任公司');
            PRAGMA user_version=2;
            """);
        Execute("INSERT OR IGNORE INTO plugin_schema_migration VALUES(2,$now,'2.0.0')", ("$now", Now));
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
        try { InitializeCalendarSchema();InitializeEquitySchema();InitializeActivityReviewSchema();InitializeExitAlertSchema();InitializeNavSchema();InitializeIndustryPeSchema();InitializeIndexSeriesSchema();InitializeMarketCycleApprovalSchema(); }
        catch { db.Dispose(); throw; }
        marketCycleApprovalAuditKey=DeriveMarketCycleApprovalAuditKey(encryptionKey);
    }
    private string Now => CRS.Timing.BeijingClock.Read(clock).ToString("O");
    /// <summary>自然日查询进度独立于交易日覆盖率，不能用游标宣称份额完整。</summary>
    public DateOnly? ReadSyncCursor()=>Scalar("SELECT checked_through FROM worker_sync_cursor WHERE id=1") is string value?DateOnly.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture):null;
    public void RecordSyncCursor(DateOnly day)
    {
        beforeWrite?.Invoke();Execute("INSERT INTO worker_sync_cursor VALUES(1,$d) ON CONFLICT(id) DO UPDATE SET checked_through=max(checked_through,excluded.checked_through)",("$d",day.ToString("yyyy-MM-dd")));
    }
    private SqliteCommand Command(string sql, params (string, object?)[] args)
    { operationCancellation.ThrowIfCancellationRequested();var command = db.CreateCommand(); command.CommandText = sql; foreach (var (key,value) in args) command.Parameters.AddWithValue(key,value??DBNull.Value); return command; }
    private void Execute(string sql, params (string, object?)[] args) { using var command = Command(sql,args); command.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string, object?)[] args) { using var command = Command(sql,args); return command.ExecuteScalar(); }
    private static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    // Compatibility helpers still used by the not-yet-extracted IndexSeriesRepository partial.
    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static string IndustryTime(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    public string RecordFetch(string hash, string sourceUrl, string? originalHash = null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1")
    {
        beforeWrite?.Invoke();
        if (!Uri.TryCreate(sourceUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length!=0 || !uri.IsDefaultPort
            || !(uri.Host=="sse.com.cn" || uri.Host.EndsWith(".sse.com.cn",StringComparison.Ordinal) || uri.Host=="szse.cn" || uri.Host.EndsWith(".szse.cn",StringComparison.Ordinal)
                ||provider=="ReviewedEquityDisclosurePdf"&&OfficialEquityDocuments.IsReviewedIssuer(uri)))
            throw new InvalidDataException("仅接受已登记的官方HTTPS来源；发行人原文须匹配固定证券、报告期及已核验哈希。");
        if (!raw.Verify(hash)) throw new InvalidDataException("Evidence is missing or damaged.");
        originalHash ??= hash;
        if (!raw.Verify(originalHash)) throw new InvalidDataException("Original official file evidence is missing or damaged.");
        var id = Guid.NewGuid().ToString("N");
        // 自动响应与人工标准化来源分别登记，历史原件和解析器版本可以追溯。
        Execute("INSERT INTO source_fetch VALUES($id,$provider,$now,NULL,$url,$hash,$original,'RawSaved',$parser)",("$id",id),("$provider",provider),("$parser",parserVersion),("$now",Now),("$url",sourceUrl),("$hash",hash),("$original",originalHash));
        return id;
    }
    public void MarkFailed(string fetchId) {beforeWrite?.Invoke();Execute("UPDATE source_fetch SET status='ParseFailed' WHERE fetch_id=$id",("$id",fetchId));}
    public void ImportCalendar(string fetchId, IReadOnlyList<(string Market,DateOnly Date)> days, CancellationToken cancellation)
    {
        beforeWrite?.Invoke();
        ValidateEvidence();
        using var transaction = db.BeginTransaction();
        foreach (var (market,date) in days)
        {
            cancellation.ThrowIfCancellationRequested();
            Execute("""
                INSERT INTO trading_day VALUES($market,$date,$fetch)
                ON CONFLICT(market,trade_date) DO UPDATE SET fetch_id=excluded.fetch_id
                WHERE EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=trading_day.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                """,("$market",market),("$date",date.ToString("yyyy-MM-dd")),("$fetch",fetchId));
        }
        Execute("UPDATE source_fetch SET status='Accepted' WHERE fetch_id=$fetch",("$fetch",fetchId)); cancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    public int ImportShares(string fetchId, IReadOnlyList<ImportRow> rows, CancellationToken cancellation,bool requireCalendar=true)
        =>ImportSharesDetailed(fetchId,rows,cancellation,requireCalendar).Added;
    /// <summary>统计已提交的新增、重复和待审核；不把收到的来源行数冒充新增事实。</summary>
    public ShareImportOutcome ImportSharesDetailed(string fetchId, IReadOnlyList<ImportRow> rows, CancellationToken cancellation,bool requireCalendar=true)
    {
        beforeWrite?.Invoke();
        // 只有标明官方JSON适配器来源的事实可以先保存；日历未核验时指标仍不可计算。
        if(!requireCalendar && Scalar("SELECT provider FROM source_fetch WHERE fetch_id=$id",("$id",fetchId)) as string is not ("OfficialSseEtfJson" or "OfficialSzseEtfJson"))throw new InvalidDataException("缺少官方适配器来源，不能跳过交易日历校验。");
        using var transaction = db.BeginTransaction(); int accepted = 0;int duplicates=0;int pending=0;
        foreach (var row in rows.OrderBy(x=>x.Date))
        {
            cancellation.ThrowIfCancellationRequested(); var date = row.Date.ToString("yyyy-MM-dd");
            var today = DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime);
            if (row.Date>today || Convert.ToInt32(Scalar("SELECT count(*) FROM watchlist_membership WHERE market=$m AND ticker=$t AND active=1",("$m",row.Key.Market),("$t",row.Key.Ticker)))!=1
                || requireCalendar && Convert.ToInt32(Scalar("SELECT count(*) FROM trading_day WHERE market=$m AND trade_date=$d",("$m",row.Key.Market),("$d",date)))!=1)
                throw new InvalidDataException("证券不在有效池、日期不在已核对交易日历内，或日期在未来。");
            var value = Decimal(EtfMetrics.Normalize(row.Original,row.Unit));
            var latest = Scalar("SELECT total_shares FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d ORDER BY revision_no DESC LIMIT 1",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var latestStatus=Scalar("SELECT status FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d ORDER BY revision_no DESC LIMIT 1",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var latestHealthy=Convert.ToInt32(Scalar("SELECT count(*) FROM share_observation_revision r JOIN source_fetch f USING(fetch_id) WHERE r.market=$m AND r.ticker=$t AND r.trade_date=$d AND r.revision_no=(SELECT max(revision_no) FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d) AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open')",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)))==1;
            if (latest==value && latestStatus!="Rejected" && latestHealthy) {duplicates++;continue;}
            var previous = Scalar("SELECT c.revision_id FROM share_accepted_current c WHERE market=$m AND ticker=$t AND trade_date=$d",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var number = Convert.ToInt32(Scalar("SELECT coalesce(max(revision_no),0)+1 FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)));
            // 质量预警可与最近已知事实比较，不能用这个日期集合代替指标的完整交易日历。
            var before = Scalar("SELECT r.total_shares FROM share_accepted_current c JOIN share_observation_revision r ON r.revision_id=c.revision_id WHERE c.market=$m AND c.ticker=$t AND c.trade_date=(SELECT max(trade_date) FROM share_accepted_current WHERE market=$m AND ticker=$t AND trade_date<$d)",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var after = Scalar("SELECT r.total_shares FROM share_accepted_current c JOIN share_observation_revision r ON r.revision_id=c.revision_id WHERE c.market=$m AND c.ticker=$t AND c.trade_date=(SELECT min(trade_date) FROM share_accepted_current WHERE market=$m AND ticker=$t AND trade_date>$d)",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var current=EtfMetrics.Normalize(row.Original,row.Unit);
            var beforeDate=Scalar("SELECT max(trade_date) FROM share_accepted_current WHERE market=$m AND ticker=$t AND trade_date<$d",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            var afterDate=Scalar("SELECT min(trade_date) FROM share_accepted_current WHERE market=$m AND ticker=$t AND trade_date>$d",("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date)) as string;
            static bool Jump(decimal previousValue,decimal nextValue)=>previousValue==0?nextValue!=0:Math.Abs(nextValue/previousValue-1)>0.2m;
            var jump = before is not null && NoKnownTradingDayBetween(row.Key,beforeDate!,date) && Jump(decimal.Parse(before,CultureInfo.InvariantCulture),current)
                || after is not null && NoKnownTradingDayBetween(row.Key,date,afterDate!) && Jump(current,decimal.Parse(after,CultureInfo.InvariantCulture));
            var review = previous is not null || jump || latest is not null;
            if(review)pending++;
            var id = Guid.NewGuid().ToString("N"); var now=Now;
            Execute("INSERT INTO share_observation_revision VALUES($id,$m,$t,$d,$v,$original,$unit,$fetch,$number,$accepted,$now,$status,$previous)",
                ("$id",id),("$m",row.Key.Market),("$t",row.Key.Ticker),("$d",date),("$v",value),("$original",Decimal(row.Original)),("$unit",row.Unit),("$fetch",fetchId),("$number",number),("$accepted",review?null:now),("$now",now),("$status",review?"NeedsReview":"Accepted"),("$previous",previous));
            if(review) Execute("INSERT INTO quality_issue VALUES($id,$rule,'1',$id,'Warning','Open',$fetch,NULL,NULL)",("$id",id),("$rule",previous is not null?"Q06":"Q08"),("$fetch",fetchId));
            else { SetCurrent(row.Key,date,id); accepted++; }
        }
        Execute("UPDATE source_fetch SET status='Parsed' WHERE fetch_id=$fetch",("$fetch",fetchId));
        Execute("INSERT INTO sync_checkpoint VALUES($fetch,'Validated',$count)",("$fetch",fetchId),("$count",accepted));MarkAlertPending(); cancellation.ThrowIfCancellationRequested();transaction.Commit(); return new(accepted,duplicates,pending);
    }
    // 跨过已核验交易日的长期差额不能冒充相邻日20%跳变。日历未知时仍保守保留原有预警。
    private bool NoKnownTradingDayBetween(EtfKey key,string from,string to)=>Convert.ToInt32(Scalar("""
        SELECT count(*) FROM trading_day d WHERE d.market=$m AND d.trade_date>$from AND d.trade_date<$to
        AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=d.fetch_id AND q.rule_id='Q15' AND q.status='Open')
        """,("$m",key.Market),("$from",from),("$to",to)))==0;
    private void SetCurrent(EtfKey key,string date,string id) => Execute("INSERT INTO share_accepted_current VALUES($m,$t,$d,$id) ON CONFLICT(market,ticker,trade_date) DO UPDATE SET revision_id=excluded.revision_id",("$m",key.Market),("$t",key.Ticker),("$d",date),("$id",id));
    public void Review(string revisionId, bool accept, string reviewer, bool comparableConfirmed = false,Guid? commandId=null)
    {
        beforeWrite?.Invoke();
        if(Committed(commandId))return;
        if(string.IsNullOrWhiteSpace(reviewer)) throw new InvalidDataException("审核人不能为空。");
        if(accept && !comparableConfirmed)throw new InvalidDataException("接受前必须核实份额可比，排除未处理的基金折算或合并。");
        var row = Evidence().Single(x=>x.RevisionId==revisionId && x.State=="NeedsReview");
        if(!raw.Verify(row.Hash) || !raw.Verify(row.OriginalHash)) throw new InvalidDataException("Evidence integrity failure.");
        using var transaction=db.BeginTransaction();
        if(accept && Convert.ToInt32(Scalar("SELECT max(revision_no) FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d",("$m",row.Market),("$t",row.Ticker),("$d",row.TradeDate)))!=row.Revision) throw new InvalidDataException("请审核该日最新候选修订，不能用旧候选覆盖新修订。");
        Execute("UPDATE share_observation_revision SET status=$state,accepted_at=$now WHERE revision_id=$id",("$state",accept?"Accepted":"Rejected"),("$now",accept?Now:null),("$id",revisionId));
        Execute("UPDATE quality_issue SET status=$state,reviewer=$who,decision_time=$now WHERE target_id=$id",("$state",accept?"Approved":"Rejected"),("$who",reviewer),("$now",Now),("$id",revisionId));
        if(accept)
        {
            Execute("UPDATE quality_issue SET status='Superseded',reviewer=$who,decision_time=$now WHERE target_id IN (SELECT revision_id FROM share_observation_revision WHERE market=$m AND ticker=$t AND trade_date=$d AND status='NeedsReview')",("$who",reviewer),("$now",Now),("$m",row.Market),("$t",row.Ticker),("$d",row.TradeDate));
            Execute("UPDATE share_observation_revision SET status='Superseded' WHERE market=$m AND ticker=$t AND trade_date=$d AND status='NeedsReview'",("$m",row.Market),("$t",row.Ticker),("$d",row.TradeDate));
            SetCurrent(new(row.Market,row.Ticker),row.TradeDate,revisionId);
        }
        MarkAlertPending();RecordCommand(commandId,"review");transaction.Commit();
    }
    public List<EvidenceRow> Evidence()
    {
        // 全池证据只依赖份额/日历/市场活跃度事实；孤立的原始抓取不能阻断仪表盘查询。
        ValidateEvidence(dashboardOnly:true);
        return ReadEvidence();
    }
    /// <summary>先过滤证券再返回证据，单只历史不会挤占全池查询的分页容量。</summary>
    public List<EvidenceRow> Evidence(EtfKey key){ValidateEvidence(key);return ReadEvidence(key);}
    private List<EvidenceRow> ReadEvidence(EtfKey? key=null,DateOnly? from=null,DateOnly? to=null,int maximumRows=-1)
    {
        using var command=Command("SELECT r.revision_id,r.market,r.ticker,r.trade_date,r.total_shares,CASE WHEN EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open') THEN 'Critical' ELSE r.status END,r.revision_no,f.sha256,f.original_sha256,f.source_url,f.retrieved_utc,r.first_seen,r.accepted_at,r.supersedes_id FROM share_observation_revision r JOIN source_fetch f ON f.fetch_id=r.fetch_id WHERE ($m IS NULL OR (r.market=$m AND r.ticker=$t)) AND ($from IS NULL OR r.trade_date>=$from) AND ($to IS NULL OR r.trade_date<=$to) ORDER BY r.trade_date DESC,r.revision_no DESC,r.revision_id LIMIT $limit",("$m",key?.Market),("$t",key?.Ticker),("$from",from?.ToString("yyyy-MM-dd")),("$to",to?.ToString("yyyy-MM-dd")),("$limit",maximumRows));
        using var reader=command.ExecuteReader(); var rows=new List<EvidenceRow>();
        while(reader.Read()){CheckReadLimit(rows.Count+1);rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt32(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13)));}
        return rows;
    }
    public List<DateOnly> Calendar(string market)
    {
        ValidateEvidence(dashboardOnly:true);
        var calendar=ReadCalendar(market);
        // 不能删除单个损坏日期，否则跨日变化可能被误算为相邻交易日变化。
        return calendar.All(x=>x.Valid)?calendar.Select(x=>x.Date).ToList():[];
    }
    private List<(DateOnly Date,bool Valid)> ReadCalendar(string market)
    {
        using var command=Command("SELECT t.trade_date,NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=t.fetch_id AND q.rule_id='Q15' AND q.status='Open') FROM trading_day t WHERE t.market=$m ORDER BY t.trade_date",("$m",market));
        using var reader=command.ExecuteReader();var days=new List<(DateOnly,bool)>();
        while(reader.Read())days.Add((DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),reader.GetInt32(1)==1));
        return days;
    }
    public List<EtfHistoryPoint> History(EtfKey? key)
    {
        if(key is { } specific) ValidateEvidence(specific,includeCalendar:true);
        else ValidateEvidence(dashboardOnly:true);
        return ReadHistory(key);
    }
    private List<EtfHistoryPoint> ReadHistory(EtfKey? key,IReadOnlyDictionary<EtfKey,List<SharePoint>>? sharedPoints=null)
    {
        var keys=key is null?Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).ToArray():[key];
        var calendars=keys.Select(x=>x.Market).Distinct().ToDictionary(x=>x,ReadCalendar);
        // 同一次只读快照冻结同一北京自然日，使用仓储时钟；跨零点只纳入新日历缺口，不生成份额事实。
        var today=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(clock).DateTime);
        return EtfHistory.Calculate(keys.Select(k=>
        {
            var calendar=calendars[k.Market];
            return new EtfHistoryInput(k,calendar.All(x=>x.Valid)?calendar.Where(x=>x.Date<=today).Select(x=>x.Date).ToArray():[],sharedPoints is null?ReadPoints(k):sharedPoints[k]);
        }).ToArray());
    }
    public List<SharePoint> Points(EtfKey key)
    {
        ValidateEvidence(key);
        return ReadPoints(key);
    }
    /// <summary>原件核验仅在本次批查询内共享；下次查询仍重新读取，不能跨查询缓存损坏结果。</summary>
    public Dictionary<EtfKey,List<SharePoint>> PointsForKeys(IReadOnlyList<EtfKey> keys)
    {ValidateEvidence(dashboardOnly:true);return keys.ToDictionary(x=>x,ReadPoints);}
    private List<SharePoint> ReadPoints(EtfKey key)
    {
        using var command=Command("""
            SELECT r.trade_date,r.total_shares,r.revision_id,f.sha256,f.original_sha256 FROM share_accepted_current c
            JOIN share_observation_revision r ON r.revision_id=c.revision_id JOIN source_fetch f ON f.fetch_id=r.fetch_id
            WHERE c.market=$m AND c.ticker=$t AND NOT EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=f.fetch_id AND q.rule_id='Q15' AND q.status='Open') AND NOT EXISTS(SELECT 1 FROM share_observation_revision pending WHERE pending.market=c.market AND pending.ticker=c.ticker AND pending.trade_date=c.trade_date AND pending.status='NeedsReview') ORDER BY c.trade_date
            """,("$m",key.Market),("$t",key.Ticker)); using var reader=command.ExecuteReader(); var rows=new List<SharePoint>();
        while(reader.Read()) rows.Add(new(DateOnly.ParseExact(reader.GetString(0),"yyyy-MM-dd",CultureInfo.InvariantCulture),decimal.Parse(reader.GetString(1),CultureInfo.InvariantCulture),null,reader.GetString(2))); return rows;
    }
    /// <summary>纯只读完整性核验：发现未受信原件时拒绝返回可能误导的金融结果，不修改质量状态。</summary>
    private void ValidateEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
    {
        var (bad,_) = ScanEvidence(shareKey,includeCalendar,activityOnly,dashboardOnly,includeAttempts);
        if(bad.Count==0)return;
        // 已由后台作业标记 Q15 的原件交给各只读查询的现有 SQL 过滤/标红；
        // 后台尚未登记的新损坏必须失败关闭，不能在查询时偷偷写状态。
        var blocked=new HashSet<string>(StringComparer.Ordinal);
        using(var query=Command("SELECT evidence_id FROM quality_issue WHERE rule_id='Q15' AND status='Open'"))
        using(var reader=query.ExecuteReader())while(reader.Read())blocked.Add(reader.GetString(0));
        if(bad.Any(id=>!blocked.Contains(id)))
            throw new InvalidDataException("Evidence integrity failure; background reconciliation required.");
    }
    /// <summary>读取原始字节和规范化字节的哈希；仅在本次操作中缓存相同哈希的结果。</summary>
    private (List<string> Bad,List<string> Good) ScanEvidence(EtfKey? shareKey=null,bool includeCalendar=false,bool activityOnly=false,bool dashboardOnly=false,bool includeAttempts=false)
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
        RecordDashboardStage("EvidenceSql",stageStarted,fetches.Count);stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        var verified=raw.VerifyMany(fetches.SelectMany(x=>new[]{x.Hash,x.Original}),operationCancellation);
        // 此阶段包含共享句柄准入、完整读取及SHA核验；不能把它全称为物理磁盘耗时。
        RecordDashboardStage("RawVerificationIncludingAdmission",stageStarted,verified.Count);stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var fetch in fetches)
        {
            operationCancellation.ThrowIfCancellationRequested();
            if(!verified[fetch.Hash]||!verified[fetch.Original])bad.Add(fetch.Id);else good.Add(fetch.Id);
        }
        RecordDashboardStage("EvidenceClassification",stageStarted,fetches.Count);return (bad,good);
    }
    /// <summary>仅由 Worker 持久后台完整性任务调用。状态变更和审计事件在单一事务中提交，重跑幂等。</summary>
    public void ReconcileEvidenceIntegrity()
    {
        var (bad,good)=ScanEvidence();
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
        MarkAlertPending();operationCancellation.ThrowIfCancellationRequested();transaction.Commit();
    }
    public List<EtfSummary> Overview()
    {
        ValidateEvidence(dashboardOnly:true);
        return ReadOverview();
    }
    private List<EtfSummary> ReadOverview(IReadOnlyDictionary<EtfKey,List<SharePoint>>? sharedPoints=null)
    {
        var securities=new List<(string Market,string Ticker,string Name,string Index,string Label)>();
        using(var command=Command("SELECT s.market,s.ticker,s.name,s.index_group,w.review_status FROM security_master s JOIN watchlist_membership w USING(market,ticker) WHERE w.active=1"))
        using(var reader=command.ExecuteReader()) while(reader.Read()) securities.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        var calendars=securities.Select(x=>x.Market).Distinct().ToDictionary(x=>x,ReadCalendar);
        var issues=ReadOverviewIssues();
        return securities.Select(s=>
        {
            var points=sharedPoints is null?ReadPoints(new(s.Market,s.Ticker)):sharedPoints[new(s.Market,s.Ticker)]; var last=points.LastOrDefault(); var calendar=calendars[s.Market];
            var calendarValid=calendar.All(x=>x.Valid);var days=calendarValid?calendar.Select(x=>x.Date).ToList():[];
            Metric Calculate(int window)=>last is null?new(null,null,"Incomplete",""):EtfMetrics.Calculate(last.Date,window,days,points);
            var issue=issues.GetValueOrDefault((s.Market,s.Ticker));
            return new EtfSummary(s.Market,s.Ticker,s.Name,s.Index,s.Label,last?.Date,last?.Shares,Calculate(1).DeltaShares,Calculate(5).DeltaShares,Calculate(20).DeltaShares,!calendarValid?"CalendarCritical":issue??(last is null?"无有效数据":Calculate(1).State),points.Count);
        }).ToList();
    }
    private Dictionary<(string Market,string Ticker),string> ReadOverviewIssues()
    {
        using var command=Command("""
            WITH issues AS (
                SELECT r.market,r.ticker,r.trade_date,r.revision_no,
                    CASE WHEN EXISTS(SELECT 1 FROM quality_issue q WHERE q.evidence_id=r.fetch_id AND q.rule_id='Q15' AND q.status='Open')
                        THEN 'Critical' ELSE r.status END AS state
                FROM share_observation_revision r), ranked AS (
                SELECT market,ticker,state,row_number() OVER(PARTITION BY market,ticker ORDER BY trade_date DESC,revision_no DESC) AS n
                FROM issues WHERE state IN ('Critical','NeedsReview'))
            SELECT market,ticker,state FROM ranked WHERE n=1
            """);
        using var reader=command.ExecuteReader();var result=new Dictionary<(string,string),string>();
        while(reader.Read()){operationCancellation.ThrowIfCancellationRequested();result.Add((reader.GetString(0),reader.GetString(1)),reader.GetString(2));}
        return result;
    }
    public CoverageReport Coverage(EtfKey key,DateOnly from,DateOnly to)
    {
        // 分母取自日历，不能从现有份额记录反推“应有日期”并掩盖缺口。
        if(from>to)throw new InvalidDataException("覆盖区间的开始日期不能晚于结束日期。");
        if(Convert.ToInt32(Scalar("SELECT count(*) FROM security_master WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker)))!=1)throw new InvalidDataException("证券未登记。");
        ValidateEvidence(key,includeCalendar:true,includeAttempts:true);
        var calendar=ReadCalendar(key.Market);var range=calendar.Where(x=>x.Date>=from && x.Date<=to).ToList();
        var points=ReadPoints(key).ToDictionary(x=>x.Date);
        var evidence=ReadEvidence(key).GroupBy(x=>DateOnly.ParseExact(x.TradeDate,"yyyy-MM-dd",CultureInfo.InvariantCulture)).ToDictionary(x=>x.Key,x=>x.ToList());
        var rows=new List<CoverageDay>();
        var allAttempts=CollectionAttempts(key,from,to);
        var attempts=allAttempts.GroupBy(x=>x.Date).ToDictionary(x=>x.Key,x=>x.First());
        foreach(var day in range)
        {
            evidence.TryGetValue(day.Date,out var revisions);
            points.TryGetValue(day.Date,out var point);
            var dayState=!day.Valid?"CalendarCritical"
                :revisions?.Any(x=>x.State=="NeedsReview")==true?"NeedsReview"
                :point is not null?"Accepted"
                :revisions?.Any(x=>x.State=="Critical")==true?"Critical"
                :revisions?.FirstOrDefault()?.State=="Rejected"?"QualityRejected":"Missing";
            attempts.TryGetValue(day.Date,out var attempt);
            rows.Add(new(day.Date,dayState,point?.RevisionId??revisions?.FirstOrDefault()?.RevisionId,attempt));
        }
        var accepted=rows.Count(x=>x.State=="Accepted");var calendarValid=calendar.Count>0 && calendar.All(x=>x.Valid);
        var boundary=calendar.Count>0 && calendar[0].Date<=from && calendar[^1].Date>=to
            || Enumerable.Range(from.DayNumber,to.DayNumber-from.DayNumber+1).All(d=>IsReviewedTradingDay(key.Market,DateOnly.FromDayNumber(d)).HasValue);
        var state=range.Count==0?"NoCalendar":!calendarValid?"CalendarCritical":!boundary?"CalendarRangeIncomplete":accepted==rows.Count?"CompleteRegisteredCalendar":"Incomplete";
        return new(key.Market,key.Ticker,from,to,rows.Count,accepted,rows.Count(x=>x.State=="Missing"),rows.Count(x=>x.State=="NeedsReview"),
            rows.Count(x=>x.State is "Critical" or "CalendarCritical"),rows.Count(x=>x.State=="QualityRejected"),calendarValid && rows.Count>0?decimal.Round(accepted*100m/rows.Count,2):null,calendarValid,boundary,state,rows);
    }
    /// <summary>完整目录含未启用条目，停用后仍能编辑和恢复；修订号保护多个窗口的并发修改。</summary>
    public List<EtfConfiguration> Configuration()
    {
        using var command=Command("SELECT s.market,s.ticker,s.name,s.index_group,w.active,w.reason,v.origin,v.revision FROM security_master s JOIN watchlist_membership w USING(market,ticker) JOIN etf_configuration_version v USING(market,ticker) ORDER BY w.active DESC,s.index_group,s.market,s.ticker");
        using var reader=command.ExecuteReader();var rows=new List<EtfConfiguration>();
        while(reader.Read())rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4)!=0,reader.GetString(5),reader.GetString(6),reader.GetInt64(7)));
        return rows;
    }
    public void SetMembership(EtfKey key,string name,string index,bool active,string reason,Guid? commandId=null)=>SaveMembership(key,name,index,active,reason,null,commandId);
    public void SaveConfiguration(EtfKey key,string name,string index,bool active,string reason,long expectedRevision,Guid? commandId=null)=>SaveMembership(key,name,index,active,reason,expectedRevision,commandId);
    private void SaveMembership(EtfKey key,string name,string index,bool active,string reason,long? expectedRevision,Guid? commandId)
    {
        beforeWrite?.Invoke();
        if(Committed(commandId))return;
        name=name.Trim();index=index.Trim();reason=reason.Trim();
        if(key.Market is not ("SSE" or "SZSE") || key.Ticker.Length!=6 || !key.Ticker.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(name) || name.Length>100 || string.IsNullOrWhiteSpace(index) || index.Length>64 || string.IsNullOrWhiteSpace(reason) || reason.Length>500) throw new InvalidDataException("市场或代码格式错误，名称、指数分组及配置理由不能为空或超过长度限制。");
        using var transaction=db.BeginTransaction();
        var version=Scalar("SELECT revision FROM etf_configuration_version WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker));
        if(expectedRevision is { } expected && (expected< -1 || expected== -1 && version is not null || expected>=0 && (version is null || Convert.ToInt64(version)!=expected)))throw new InvalidDataException("配置已存在或被其它窗口修改，请刷新后重新选择。");
        var oldName=Scalar("SELECT name FROM security_master WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker));
        var oldIndex=Scalar("SELECT index_group FROM security_master WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker));
        // 原实现忽略已存在代码，导致名称和指数无法修改；现在以同一稳定标识更新元数据。
        Execute("INSERT INTO security_master VALUES($m,$t,$name,$index) ON CONFLICT(market,ticker) DO UPDATE SET name=excluded.name,index_group=excluded.index_group",("$m",key.Market),("$t",key.Ticker),("$name",name),("$index",index));
        // 已存在条目按稳定标识更新，启停只改变采集范围，不删除配置或历史事实。
        var membership=Scalar("SELECT active FROM watchlist_membership WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker));
        Execute(membership is null?"INSERT INTO watchlist_membership VALUES($m,$t,$active,$reason,'DataPilot',NULL)":"UPDATE watchlist_membership SET active=$active,reason=$reason WHERE market=$m AND ticker=$t",("$m",key.Market),("$t",key.Ticker),("$active",active?1:0),("$reason",reason));
        Execute("INSERT INTO etf_configuration_version VALUES($m,$t,1,'用户配置') ON CONFLICT(market,ticker) DO UPDATE SET revision=revision+1,origin='用户配置'",("$m",key.Market),("$t",key.Ticker));
        var change=Guid.NewGuid().ToString("N");
        Execute("INSERT INTO watchlist_change VALUES($id,$m,$t,$active,$reason,$now)",("$id",change),("$m",key.Market),("$t",key.Ticker),("$active",active?1:0),("$reason",reason),("$now",Now));
        Execute("INSERT INTO etf_metadata_change VALUES($id,$m,$t,$oldName,$oldIndex,$name,$index,$now)",("$id",change),("$m",key.Market),("$t",key.Ticker),("$oldName",oldName),("$oldIndex",oldIndex),("$name",name),("$index",index),("$now",Now));
        RecordCommand(commandId,"watchlist");transaction.Commit();
    }
    /// <summary>业务事实与稳定命令标识同事务，调度器重放已提交命令时不再写第二次。</summary>
    private bool Committed(Guid? id)=>id is { } value && Convert.ToInt64(Scalar("SELECT count(*) FROM worker_business_command WHERE request=$r",("$r",value.ToString("N"))))>0;
    private void RecordCommand(Guid? id,string operation)
    {if(id is { } value)Execute("INSERT INTO worker_business_command VALUES($r,$o,$now)",("$r",value.ToString("N")),("$o",operation),("$now",Now));}
    public void Snapshot(string target)
    {
        using var destination=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=target,Password=new SqliteConnectionStringBuilder(db.ConnectionString).Password,Pooling=false}.ToString()); destination.Open(); db.BackupDatabase(destination);
        using var check=destination.CreateCommand(); check.CommandText="PRAGMA integrity_check;";
        if(check.ExecuteScalar() as string!="ok") throw new InvalidDataException("Snapshot integrity failure.");
    }
    public void Dispose(){System.Security.Cryptography.CryptographicOperations.ZeroMemory(marketCycleApprovalAuditKey);db.Dispose();}
}
