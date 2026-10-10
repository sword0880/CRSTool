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
    private readonly string connectionString;
    /// <summary>打开本地 SQLite 存储并初始化版本化表结构，不向仓库写入账户资料。</summary>
    public LocalStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRS", "data");
        Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "crs.db"), ForeignKeys = true }.ToString();
        using var connection = Open();
        // 表结构与业务版本分开记录；未识别的数据库版本拒绝降级写入。
        if (connection.ExecuteScalar<int>("PRAGMA user_version") > 1) throw new CrsException("数据库版本高于当前程序，请使用较新版本。");
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
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    /// <summary>在事务中保存计算结果和可用结转，参数化写入避免说明文本改变 SQL。</summary>
    public void Save(CalculationResult r)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        c.Execute("INSERT INTO calculations(id,year,created_utc,complete,issues,result_json,carry) VALUES(@Id,@Year,@Utc,@Complete,@Issues,@Json,@Carry)",
            new { Id = r.SnapshotId, r.Year, Utc = DateTimeOffset.UtcNow.ToString("O"), Complete = r.Complete ? 1 : 0,
                Issues = r.Issues.Count, Json = JsonSerializer.Serialize(r), Carry = r.CarryEligible ? CarryFiles.Encode(CarryFiles.Create(r)) : null }, tx);
        tx.Commit();
    }
    /// <summary>读取最近一百份计算的索引信息供历史窗口选择。</summary>
    public List<HistoryItem> History()
    {
        using var c = Open();
        // SQLite INTEGER 返回 Int64，先映射可写行模型再构造领域记录，避免 record 构造类型不匹配。
        return c.Query<HistoryRow>("SELECT id AS Id,year AS Year,created_utc AS CreatedUtc,complete AS Complete,issues AS Issues,COALESCE(json_extract(result_json, '$.Broker'), '未登记') AS Broker FROM calculations ORDER BY created_utc DESC LIMIT 100")
            .Select(r => new HistoryItem(r.Id, r.Year, r.CreatedUtc, r.Complete == 1, r.Issues, r.Broker)).ToList();
    }
    /// <summary>按计算编号恢复历史结果，历史底稿保持原始汇率与状态。</summary>
    public CalculationResult Load(string id)
    {
        using var c = Open();
        var json = c.QuerySingleOrDefault<string>("SELECT result_json FROM calculations WHERE id=@Id", new { Id = id }) ?? throw new CrsException("找不到计算记录。");
        return JsonSerializer.Deserialize<CalculationResult>(json) ?? throw new CrsException("计算记录无效。");
    }
    /// <summary>追加与指定快照绑定的人工依据，不覆盖原始问题及计算金额。</summary>
    public void AddReview(string id, string category, string evidence)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 4000) throw new CrsException("请填写复核类型及不超过 4000 字的依据。");
        using var c = Open();
        c.Execute("INSERT INTO reviews(snapshot_id,category,evidence,created_utc) VALUES(@Id,@Category,@Evidence,@Utc)",
            new { Id = id, Category = category.Trim(), Evidence = evidence.Trim(), Utc = DateTimeOffset.UtcNow.ToString("O") });
    }
    /// <summary>读取当前快照的复核依据及 UTC 保存时间。</summary>
    public List<ReviewNote> Reviews(string id)
    {
        using var c = Open();
        return c.Query<ReviewNote>("SELECT snapshot_id AS SnapshotId,category AS Category,evidence AS Evidence,created_utc AS CreatedUtc FROM reviews WHERE snapshot_id=@Id ORDER BY id", new { Id = id }).ToList();
    }
    /// <summary>持久化行映射只存在基础设施层，数据库类型不进入领域契约。</summary>
    private sealed class HistoryRow
    {
        public string Broker { get; set; } = "未登记";
        public string Id { get; set; } = "";
        public int Year { get; set; }
        public string CreatedUtc { get; set; } = "";
        public int Complete { get; set; }
        public int Issues { get; set; }
    }
}
