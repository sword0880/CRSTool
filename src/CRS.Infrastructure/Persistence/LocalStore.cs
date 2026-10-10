using Microsoft.Data.Sqlite;
using Dapper;
using System.Text.Json;
using CRS.Domain;
using CRS.Application.Abstractions;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace CRS.Infrastructure;

public sealed class LocalStore : ICalculationRepository
{
    public string DirectoryPath { get; }
    private readonly VaultSession session;
    /// <summary>打开本地 SQLite 存储并初始化版本化表结构，不向仓库写入账户资料。</summary>
    internal LocalStore(VaultSession session,bool create=false)
    {
        this.session=session; DirectoryPath=session.DirectoryPath;
        using var lease=session.Enter();
        using var connection = session.Open(create:create);
        // 表结构与业务版本分开记录；未识别的数据库版本拒绝降级写入。
        if (connection.ExecuteScalar<int>("PRAGMA user_version") > 1) throw new CrsException("数据库版本高于当前程序，请使用较新版本。");
        if (!create)
        {
            // 解锁不得把未知数据库静默变成新的空业务库。
            if(connection.ExecuteScalar<int>("PRAGMA user_version")!=1
                || connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('calculations','reviews')")!=2)
                throw new CrsException("保险库业务格式无效或版本不支持。");
            return;
        }
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS calculations(id TEXT PRIMARY KEY, year INTEGER NOT NULL, created_utc TEXT NOT NULL,
              complete INTEGER NOT NULL, issues INTEGER NOT NULL, result_json TEXT NOT NULL, carry BLOB);
            CREATE TABLE IF NOT EXISTS reviews(id INTEGER PRIMARY KEY, snapshot_id TEXT NOT NULL REFERENCES calculations(id),
              category TEXT NOT NULL, evidence TEXT NOT NULL, created_utc TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
    }
    /// <summary>配置 NLog 本地轮转日志，日志正文仅由调用方提供脱敏操作信息。</summary>
    public static void ConfigureLogging(string directory, bool enabled = true, string minimumLevel = "Info",
        int retentionFiles = 14, int fileSizeMb = 1, bool exactLogDirectory = false)
    {
        var config = new LoggingConfiguration();
        if (enabled)
        {
            var target = new FileTarget("local") { FileName = Path.Combine(exactLogDirectory ? directory : Path.Combine(directory, "logs"), "crs.log"),
                Layout = "${longdate}|${level}|${message}", ArchiveAboveSize = fileSizeMb * 1024L * 1024, MaxArchiveFiles = retentionFiles };
            config.AddRule(LogLevel.FromString(minimumLevel), LogLevel.Fatal, target);
        }
        LogManager.Configuration = config;
    }
    /// <summary>创建启用外键的数据库连接；每次操作自行释放连接。</summary>
    private SqliteConnection Open()=>session.Open();
    /// <summary>在事务中保存计算结果和可用结转，参数化写入避免说明文本改变 SQL。</summary>
    public void Save(CalculationResult r)
    {
        // 整个事务受解锁会话门闩保护，锁定后旧后台任务无法追加结果。
        using var lease=session.Enter();
        using var c = Open(); using var tx = c.BeginTransaction();
        // 复算只能追加到已存在的父运行，避免保存断链历史或覆盖原任务。
        if (r.ParentSnapshotId is { } parent && (parent == r.SnapshotId
            || c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id=@Parent AND year=@Year", new { Parent = parent, r.Year }, tx) != 1))
            throw new CrsException("复算父运行不存在或年度不一致，不能保存。");
        // 来源任务必须存在且属于相同年度，汇总记录与来源关系在同一事务中保存。
        foreach (var source in r.AggregationSources)
            if (source.SnapshotId == r.SnapshotId || source.Year != r.Year
                || c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations WHERE id=@Id AND year=@Year", new { Id = source.SnapshotId, r.Year }, tx) != 1)
                throw new CrsException("年度汇总来源不存在或年度不一致，不能保存。");
        c.Execute("INSERT INTO calculations(id,year,created_utc,complete,issues,result_json,carry) VALUES(@Id,@Year,@Utc,@Complete,@Issues,@Json,@Carry)",
            new { Id = r.SnapshotId, r.Year, Utc = DateTimeOffset.UtcNow.ToString("O"), Complete = r.Complete ? 1 : 0,
                Issues = r.Issues.Count, Json = JsonSerializer.Serialize(r), Carry = r.CarryEligible ? CarryFiles.Encode(CarryFiles.Create(r)) : null }, tx);
        tx.Commit();
    }
    /// <summary>保留最近任务查询，完整历史由分页入口提供。</summary>
    public List<HistoryItem> History() => QueryHistory(1, 100).Rows;

    /// <summary>计数和分页在同一读取事务中完成，同一时间以运行编号稳定排序。</summary>
    public HistoryPageResult QueryHistory(int pageNumber, int pageSize)
    {
        using var lease=session.Enter();
        if (pageNumber < 1 || pageSize is < 1 or > 100 || (long)(pageNumber - 1) * pageSize > int.MaxValue)
            throw new CrsException("历史页码或每页数量无效。");
        using var c = Open(); using var tx = c.BeginTransaction();
        var total = c.ExecuteScalar<int>("SELECT COUNT(*) FROM calculations", transaction: tx);
        // 总数减少时回到最后有效页，刷新后不显示空的越界页。
        var page = Math.Min(pageNumber, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        // SQLite INTEGER 返回 Int64，先映射可写行模型再构造领域记录，避免 record 构造类型不匹配。
        var rows = c.Query<HistoryRow>("SELECT id AS Id,year AS Year,created_utc AS CreatedUtc,complete AS Complete,issues AS Issues,COALESCE(json_extract(result_json, '$.Broker'), '未登记') AS Broker,json_extract(result_json, '$.ParentSnapshotId') AS ParentSnapshotId,COALESCE(json_array_length(json_extract(result_json, '$.AggregationSources')),0) AS AggregationSourceCount,COALESCE(json_extract(result_json, '$.CoveredAccounts'),'[]') AS AccountsJson FROM calculations ORDER BY created_utc DESC,id DESC LIMIT @Limit OFFSET @Offset",
            new { Limit = pageSize, Offset = (page - 1) * pageSize }, tx)
            .Select(r => new HistoryItem(r.Id, r.Year, r.CreatedUtc, r.Complete == 1, r.Issues, r.Broker, r.ParentSnapshotId, r.AggregationSourceCount > 0 || r.Broker == "MULTI",
                string.Join("、",JsonSerializer.Deserialize<string[]>(r.AccountsJson) ?? []))).ToList();
        return new(rows, total, page, pageSize);
    }
    /// <summary>按计算编号恢复历史结果，历史底稿保持原始汇率与状态。</summary>
    public CalculationResult Load(string id)
    {
        using var lease=session.Enter();
        using var c = Open();
        var json = c.QuerySingleOrDefault<string>("SELECT result_json FROM calculations WHERE id=@Id", new { Id = id }) ?? throw new CrsException("找不到计算记录。");
        return JsonSerializer.Deserialize<CalculationResult>(json) ?? throw new CrsException("计算记录无效。");
    }
    /// <summary>追加与指定快照绑定的人工依据，不覆盖原始问题及计算金额。</summary>
    public void AddReview(string id, string category, string evidence)
    {
        using var lease=session.Enter();
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 4000) throw new CrsException("请填写复核类型及不超过 4000 字的依据。");
        using var c = Open();
        c.Execute("INSERT INTO reviews(snapshot_id,category,evidence,created_utc) VALUES(@Id,@Category,@Evidence,@Utc)",
            new { Id = id, Category = category.Trim(), Evidence = evidence.Trim(), Utc = DateTimeOffset.UtcNow.ToString("O") });
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
        public int AggregationSourceCount { get; set; }
        public string AccountsJson { get; set; } = "[]";
        public int Year { get; set; }
        public string CreatedUtc { get; set; } = "";
        public int Complete { get; set; }
        public int Issues { get; set; }
    }
}
