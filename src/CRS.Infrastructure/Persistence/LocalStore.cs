using Microsoft.Data.Sqlite;
using Dapper;
using System.Text.Json;
using CRS.Domain;
using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

public sealed class LocalStore : ICalculationRepository
{
    public string DirectoryPath { get; }
    private readonly VaultSession session;
    private static readonly Lazy<(string App, string Domain, string Infrastructure)> ExecutionBuild = new(() => (
        RunEvidenceSeal.AssemblyHash(typeof(CalculationResult).Assembly.Location),
        RunEvidenceSeal.AssemblyHash(typeof(FifoEngine).Assembly.Location),
        RunEvidenceSeal.AssemblyHash(typeof(LocalStore).Assembly.Location)));
    /// <summary>打开本地 SQLite 存储并初始化版本化表结构，不向仓库写入账户资料。</summary>
    internal LocalStore(VaultSession session,bool create=false)
    {
        this.session=session; DirectoryPath=session.DirectoryPath;
        using var lease=session.Enter();
        using var connection = session.Open(create:create);
        DatabaseSchema.Ensure(session,connection,create);
    }
    /// <summary>创建启用外键的数据库连接；每次操作自行释放连接。</summary>
    private SqliteConnection Open()=>session.Open();
    /// <summary>在事务中保存计算结果和可用结转，参数化写入避免说明文本改变 SQL。</summary>
    public void Save(CalculationResult r) => SaveImport(r, true);

    public void Save(CalculationResult r, CancellationToken token) => SaveImport(r,true,token);

    public void SaveImport(CalculationResult r, bool allowDuplicate) => SaveImport(r,allowDuplicate,default);

    private void SaveImport(CalculationResult r, bool allowDuplicate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var lease=session.Enter();using var c=Open();using var tx=c.BeginTransaction();
        Save(c,tx,r,allowDuplicate);
        token.ThrowIfCancellationRequested();
        session.Lifetime.ThrowIfCancellationRequested();
        tx.Commit();
    }
    internal void Save(SqliteConnection c,SqliteTransaction tx,CalculationResult r,bool allowDuplicate=true)
    {
        // 所有历史入口都冻结结果，人工复核通过独立表另存，不改写核算事实。
        r.Freeze();
        // 无效大载荷应在生成封存编码前被拒绝，而不是先复制整个载荷再检查。
        _ = PersistedResultLimits.Encode(r);
        r.SealForPersistence(RunEvidenceSeal.AssemblyHash(typeof(LocalStore).Assembly.Location));
        var payload = PersistedResultLimits.Encode(r);
        // 整个事务受解锁会话门闩保护，锁定后旧后台任务无法追加结果。
        // 复算只能追加到已存在的父运行，避免保存断链历史或覆盖原任务。
        if (r.ParentSnapshotId is { } parent && (parent == r.SnapshotId
            || c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id=@Parent AND year=@Year", new { Parent = parent, r.Year }, tx) != 1))
            throw new CrsException("复算父运行不存在或年度不一致，不能保存。");
        if (r.RecalculatedFromSnapshotId is { } origin && (origin == r.SnapshotId
            || c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id=@origin AND year=@Year",
                new { origin, r.Year }, tx) != 1))
            throw new CrsException("当前规则重算的来源任务不存在或年度不一致，不能保存。");
        // 来源任务必须存在且属于相同年度，汇总记录与来源关系在同一事务中保存。
        foreach (var source in r.AggregationSources)
            if (source.SnapshotId == r.SnapshotId || source.Year != r.Year
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id=@Id AND year=@Year", new { Id = source.SnapshotId, r.Year }, tx) != 1)
                throw new CrsException("年度汇总来源不存在或年度不一致，不能保存。");
        c.Execute("INSERT INTO calculations(id,year,created_utc,complete,issues,result_json,carry,execution_archived) VALUES(@Id,@Year,@Utc,@Complete,@Issues,@Json,@Carry,1)",
            new { Id = r.SnapshotId, r.Year, Utc = CRS.Timing.BeijingClock.Now.ToString("O"), Complete = r.Complete ? 1 : 0,
                Issues = r.Issues.Count, Json = System.Text.Encoding.UTF8.GetString(payload), Carry = r.CarryEligible ? CarryFiles.Encode(CarryFiles.Create(r)) : null }, tx);
        ImportHistoryIndex.Add(c,tx,r.SnapshotId,allowDuplicate);
        // 文件归档和计算事实同时提交，缺失或不匹配时不留下孤立任务。
        ExecutionArchive.Save(c,tx,r);
        // 归档也会增长数据库；提交前按实际逻辑页数检查，超出可备份范围就回滚本次任务。
        if (c.ExecuteScalar<long>("PRAGMA page_count",transaction:tx) * c.ExecuteScalar<long>("PRAGMA page_size",transaction:tx)
            > ChunkedVaultBackup.MaxDatabaseBytes)
            throw new CrsException("保存后数据库会超过 2 GiB 备份上限，本次任务已取消，原历史保留。");
    }
    /// <summary>保留最近任务查询，完整历史由分页入口提供。</summary>
    public List<HistoryItem> History() => QueryHistory(1, 100).Rows;

    /// <summary>计数和分页在同一读取事务中完成，同一时间以运行编号稳定排序。</summary>
    public HistoryPageResult QueryHistory(int pageNumber, int pageSize) => QueryHistory(pageNumber,pageSize,false);

    public HistoryPageResult QueryArchivedHistory(int pageNumber, int pageSize) => QueryHistory(pageNumber,pageSize,true);

    private HistoryPageResult QueryHistory(int pageNumber, int pageSize, bool archived)
    {
        using var lease=session.Enter();
        if (pageNumber < 1 || pageSize is < 1 or > 100 || (long)(pageNumber - 1) * pageSize > int.MaxValue)
            throw new CrsException("历史页码或每页数量无效。");
        using var c = Open(); using var tx = c.BeginTransaction();
        var total = c.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM calculations AS c LEFT JOIN calculation_import_history AS h ON h.snapshot_id=c.id
            WHERE COALESCE(h.archived,0)=@Archived
            """, new { Archived = archived }, tx);
        // 总数减少时回到最后有效页，刷新后不显示空的越界页。
        var page = Math.Min(pageNumber, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var build = ExecutionBuild.Value;
        // SQLite INTEGER 返回 Int64，先映射可写行模型再构造领域记录，避免 record 构造类型不匹配。
        // 先限制本页，再处理 JSON；超大或损坏单条记录仍占据原历史位置，但不拖垮整页。
        var rows = c.Query<HistoryRow>("""
            WITH page AS MATERIALIZED (
              SELECT c.id,h.archive_group FROM calculations AS c LEFT JOIN calculation_import_history AS h ON h.snapshot_id=c.id
              WHERE COALESCE(h.archived,0)=@Archived ORDER BY julianday(created_utc) DESC,c.id DESC LIMIT @Limit OFFSET @Offset
            ),
            bounded AS (SELECT calculations.*,CASE WHEN length(CAST(result_json AS BLOB)) BETWEEN 1 AND @MaxBytes
              THEN CASE WHEN json_valid(result_json) THEN result_json ELSE NULL END ELSE NULL END AS safe_json,
              page.archive_group
              FROM calculations INNER JOIN page ON calculations.id=page.id)
            SELECT id AS Id,year AS Year,created_utc AS CreatedUtc,complete AS Complete,issues AS Issues,
              COALESCE(substr(json_extract(safe_json,'$.Broker'),1,32),CASE WHEN safe_json IS NULL THEN '记录异常' ELSE '未登记' END) AS Broker,
              substr(json_extract(safe_json,'$.ParentSnapshotId'),1,128) AS ParentSnapshotId,
              substr(json_extract(safe_json,'$.RecalculatedFromSnapshotId'),1,128) AS RecalculatedFromSnapshotId,
              COALESCE(json_array_length(json_extract(safe_json,'$.AggregationSources')),0) AS AggregationSourceCount,
              CASE WHEN length(CAST(json_extract(safe_json,'$.CoveredAccounts') AS BLOB))<=262144
                THEN json_extract(safe_json,'$.CoveredAccounts') ELSE '[]' END AS AccountsJson,
              (json_extract(safe_json,'$.IsReplayable') IS 1
                AND json_extract(safe_json,'$.InputRecoveryMode') IS @ReplayMode
                AND COALESCE(length(trim(json_extract(safe_json,'$.CanonicalInputJson'))),0)>0) AS HasReplaySnapshot,
              (json_extract(safe_json,'$.EvidenceSeal') IS NULL OR (
                json_extract(safe_json,'$.EvidenceSeal.ApplicationSha256') IS @App
                AND json_extract(safe_json,'$.EvidenceSeal.DomainSha256') IS @Domain
                AND json_extract(safe_json,'$.EvidenceSeal.InfrastructureSha256') IS @Infrastructure)) AS ReplayBuildMatches,
              substr(archive_group,1,8) AS DuplicateGroup,
              (SELECT COUNT(*) FROM calculation_import_history AS h
                WHERE h.archive_group=bounded.archive_group AND h.archived=0) AS ActiveCopies,
              (SELECT COUNT(*) FROM calculation_import_history AS h
                WHERE h.archive_group=bounded.archive_group) AS TotalCopies
            FROM bounded ORDER BY julianday(created_utc) DESC,id DESC
            """,
            new { Limit = pageSize, Offset = (page - 1) * pageSize, MaxBytes=PersistedResultLimits.MaxBytes,
                Archived=archived, ReplayMode=(int)InputRecoveryMode.CanonicalLocalSnapshot, build.App, build.Domain, build.Infrastructure }, tx)
            .Select(r => new HistoryItem(r.Id, r.Year, CRS.Timing.BeijingClock.NormalizeText(r.CreatedUtc), r.Complete == 1, r.Issues, r.Broker, r.ParentSnapshotId, r.AggregationSourceCount > 0 || r.Broker == "MULTI",
                string.Join("、",ReadAccounts(r.AccountsJson)))
                { DuplicateGroup=r.TotalCopies>1?r.DuplicateGroup:"", ActiveCopies=r.ActiveCopies,
                    IsArchived=archived, RecalculatedFromSnapshotId=r.RecalculatedFromSnapshotId,
                    ReplayAvailability = !r.HasReplaySnapshot ? HistoryReplayAvailability.NoSnapshot
                        : r.ReplayBuildMatches ? HistoryReplayAvailability.Available : HistoryReplayAvailability.DifferentBuild }).ToList();
        return new(rows, total, page, pageSize);
    }

    public ImportReview? FindImportMatch(CalculationResult result, CancellationToken cancellation)
    {
        using var lease=session.Enter(); using var c=Open(); using var tx=c.BeginTransaction(deferred:true);
        cancellation.ThrowIfCancellationRequested();
        return ImportHistoryIndex.Find(c,tx,result,cancellation);
    }

    public IReadOnlyList<ImportHistoryComparison> CompareImportHistory(string id, CancellationToken token)
    {
        using var lease=session.Enter(); using var c=Open(); using var tx=c.BeginTransaction(deferred:true);
        return ImportHistoryIndex.Compare(c,tx,id,token);
    }

    public CarryDocument? ReadHistoricalCarry(string id, CancellationToken token)
    {
        using var lease=session.Enter(); using var c=Open();
        token.ThrowIfCancellationRequested();
        var bytes = c.ExecuteScalar<byte[]>("SELECT carry FROM calculations WHERE id=@id AND length(carry) BETWEEN 1 AND @MaxBytes",
            new { id, CarryFiles.MaxBytes });
        token.ThrowIfCancellationRequested();
        try { return bytes is null ? null : CarryFiles.Decode(bytes); }
        catch (CrsException) { return null; }
    }

    public void SetHistoryArchived(string id, bool archived)
    {
        using var lease=session.Enter(); using var c=Open(); using var tx=c.BeginTransaction();
        var changed = c.Execute("""
            UPDATE calculation_import_history AS current SET archived=@Archived WHERE snapshot_id=@Id
              AND (@Archived=0 OR current.archived=1 OR EXISTS(
                SELECT 1 FROM calculation_import_history AS other
                WHERE other.archive_group=current.archive_group
                  AND other.archived=0 AND other.snapshot_id<>current.snapshot_id))
            """,new { Id=id, Archived=archived },tx);
        if(changed!=1) throw new CrsException("只能归档有另一条在用副本的重复任务，每组须至少保留一条。");
        session.Lifetime.ThrowIfCancellationRequested();
        tx.Commit();
    }
    // 列表元数据不是业务正文；损坏的账户结构仅显示为空，完整读取仍必须拒绝坏记录。
    private static string[] ReadAccounts(string? json)
    {
        if (json is null) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; } catch(JsonException) { return []; }
    }
    /// <summary>按计算编号恢复历史结果，历史底稿保持原始汇率与状态。</summary>
    public CalculationResult Load(string id)
    {
        using var lease=session.Enter();
        using var c = Open();
        using var tx=c.BeginTransaction(deferred:true);
        var archived=c.ExecuteScalar<int?>("SELECT execution_archived FROM calculations WHERE id=@Id",new {Id=id},tx)??throw new CrsException("找不到计算记录。");
        // 在取出正文前用数据库字节长度拒绝超大记录；与读取共用同一事务，避免检查后被替换。
        var size = c.ExecuteScalar<long>("SELECT length(CAST(result_json AS BLOB)) FROM calculations WHERE id=@Id", new { Id = id },tx);
        if (size is <= 0 or > PersistedResultLimits.MaxBytes) throw new CrsException("历史记录为空或超过 128 MiB，未加载。");
        var json = c.QuerySingle<byte[]>("SELECT CAST(result_json AS BLOB) FROM calculations WHERE id=@Id", new { Id = id },tx);
        PersistedResultLimits.Validate(json);
        var result=JsonSerializer.Deserialize<CalculationResult>(json) ?? throw new CrsException("计算记录无效。");
        // 从数据库读取后先核验证据，再交付冻结结果，避免损坏记录进入界面或导出。
        RunEvidenceSeal.Verify(result);
        if(result.SnapshotId!=id || archived is not (0 or 1)) throw new CrsException("运行编号或归档状态无效。");
        if(archived==1) ExecutionArchive.Verify(c,tx,id,result.EvidenceSeal);
        tx.Commit();
        return result.Freeze();
    }
    /// <summary>追加与指定快照绑定的人工依据，不覆盖原始问题及计算金额。</summary>
    public void AddReview(string id, string category, string evidence)
    {
        using var lease=session.Enter();
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 4000) throw new CrsException("请填写复核类型及不超过 4000 字的依据。");
        using var c = Open();
        c.Execute("INSERT INTO reviews(snapshot_id,category,evidence,created_utc) VALUES(@Id,@Category,@Evidence,@Utc)",
            new { Id = id, Category = category.Trim(), Evidence = evidence.Trim(), Utc = CRS.Timing.BeijingClock.Now.ToString("O") });
    }
    /// <summary>读取当前快照的复核依据及 UTC 保存时间。</summary>
    public List<ReviewNote> Reviews(string id)
    {
        using var lease=session.Enter();
        using var c = Open();
        return c.Query<ReviewNote>("SELECT snapshot_id AS SnapshotId,category AS Category,evidence AS Evidence,created_utc AS CreatedUtc FROM reviews WHERE snapshot_id=@Id ORDER BY id", new { Id = id }).ToList();
    }
    /// <summary>持久化行映射只存在基础设施层，数据库类型不进入领域契约。</summary>
    private sealed class HistoryRow
    {
        public string Broker { get; set; } = "未登记";
        public string Id { get; set; } = "";
        public string? ParentSnapshotId { get; set; }
        public string? RecalculatedFromSnapshotId { get; set; }
        public int AggregationSourceCount { get; set; }
        public string AccountsJson { get; set; } = "[]";
        public int Year { get; set; }
        public string CreatedUtc { get; set; } = "";
        public int Complete { get; set; }
        public int Issues { get; set; }
        public string DuplicateGroup { get; set; } = "";
        public int ActiveCopies { get; set; }
        public int TotalCopies { get; set; }
        public bool HasReplaySnapshot { get; set; }
        public bool ReplayBuildMatches { get; set; }
    }
}
