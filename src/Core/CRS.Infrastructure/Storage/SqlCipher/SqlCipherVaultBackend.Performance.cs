using CRS.Application.Performance;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CRS.Infrastructure;

internal sealed partial class SqlCipherVaultBackend
{
    public IReadOnlySet<string> ArchivedOriginalHashes(string[] hashes,Action<Action> originalSession,CancellationToken token)
        => ReadArchivedOriginals(originalSession,token,new HashSet<string>(StringComparer.OrdinalIgnoreCase),c =>
            OriginalArchiveTables(c).SelectMany(table => c.Query<string>(
                $"SELECT hash FROM {table} WHERE hash IN @hashes AND length(raw) BETWEEN 1 AND 26214400", new { hashes }))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public FrozenInput? ReadArchivedOriginal(string name,string hash,Action<Action> originalSession,CancellationToken token)
        => ReadArchivedOriginals<FrozenInput?>(originalSession,token,null,c =>
        {
            var bytes = OriginalArchiveTables(c).Select(table => c.ExecuteScalar<byte[]>(
                $"SELECT raw FROM {table} WHERE hash=@hash AND length(raw) BETWEEN 1 AND 26214400", new { hash }))
                .FirstOrDefault(raw => raw is not null);
            return bytes is null ? null : FrozenInput.FromBytes(name,bytes);
        });

    private static string[] OriginalArchiveTables(SqliteConnection c)
        => c.Query<string>("""
            SELECT name FROM sqlite_master WHERE type='table'
              AND name IN ('tax_income_originals','performance_documents') ORDER BY name DESC
            """).ToArray();

    private T ReadArchivedOriginals<T>(Action<Action> originalSession,CancellationToken token,T empty,Func<SqliteConnection,T> read)
    {
        var result = empty;
        originalSession(() =>
        {
            token.ThrowIfCancellationRequested();
            var active = Required();
            using var lease = active.Enter();
            using var c = active.Open();
            result = read(c);
            token.ThrowIfCancellationRequested();
        });
        return result;
    }

    public PerformanceDocument PrepareSharedPerformance(FrozenInput input,string broker,CancellationToken cancellation,Action<Action> originalSession,BrokerSourceDocument? sourceFacts=null)
    {
        // 这里只构造待提交投影，不初始化绩效表或写入原件，后续解析失败时可直接丢弃。
        PerformanceDocument? document=null;
        lock(gate)
        {
            originalSession(()=>
            {
                var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this,loggerFactory:loggerFactory),active.Lifetime);
                document=new PerformanceFiles(CaptureGuard(),exports).Read(input,broker,cancellation,sourceFacts);
                cancellation.ThrowIfCancellationRequested();
            });
        }
        return document!;
    }
    public void CommitCalculation(CalculationResult result,IReadOnlyList<PerformanceDocument> documents,FrozenInput? incomeOriginal,
        Action<Action> originalSession,CancellationToken token,bool allowDuplicate)
    {
        // 两个模块共用同一连接和事务；任一保存失败或提交前取消，双方业务写入同时回滚。
        lock(gate)originalSession(()=>
        {
            var active=Required();token.ThrowIfCancellationRequested();
            var performance = documents.Count == 0 ? null : new PerformanceStore(active);
            using var lease=active.Enter();using var c=active.Open();using var tx=c.BeginTransaction();
            foreach(var document in documents){token.ThrowIfCancellationRequested();performance!.Import(c,tx,document);}
            if (incomeOriginal is not null) SaveIncomeOriginal(c, tx, incomeOriginal);
            repository!.Save(c,tx,result,allowDuplicate);
            token.ThrowIfCancellationRequested();active.Lifetime.ThrowIfCancellationRequested();tx.Commit();
        });
    }

    private static void SaveIncomeOriginal(SqliteConnection c, SqliteTransaction tx, FrozenInput input)
    {
        c.Execute("""
            CREATE TABLE IF NOT EXISTS tax_income_originals (
              hash TEXT PRIMARY KEY NOT NULL,
              raw BLOB NOT NULL CHECK(length(raw) BETWEEN 1 AND 26214400));
            """, transaction: tx);
        var hash = input.Sha256;
        if (c.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM tax_income_originals WHERE hash=@hash)", new { hash }, tx) != 0)
            return;
        var raw = input.CopyBytes();
        try { c.Execute("INSERT INTO tax_income_originals(hash,raw) VALUES(@hash,@raw)", new { hash, raw }, tx); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); }
    }
    /// <summary>绑定真实会话；online 决定是否提供行情适配器，绩效页面启用在线访问，锁定后取消。</summary>
    public IPerformanceUseCases CreatePerformanceUseCases(bool online=true)
    {
        lock(gate)
        {
            var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this,loggerFactory:loggerFactory),active.Lifetime);
            return new PerformanceUseCases(new PerformanceStore(active),new PerformanceFiles(CaptureGuard(),exports),active.Lifetime,
                online ? new YahooPerformanceMarketData() : null, online ? new OfficialAnnualExchangeRates().ReadCachedValuationRates : null);
        }
    }
}
