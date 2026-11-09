using CRS.Application.Performance;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CRS.Infrastructure;

internal sealed partial class SqlCipherVaultBackend
{
    public IReadOnlySet<string> ArchivedOriginalHashes(string[] hashes,Action<Action> originalSession,CancellationToken token)
        => ReadArchivedOriginals(originalSession,token,new HashSet<string>(StringComparer.OrdinalIgnoreCase),c =>
            c.Query<string>("SELECT hash FROM performance_documents WHERE hash IN @hashes AND length(raw) BETWEEN 1 AND 26214400",
                new { hashes }).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public FrozenInput? ReadArchivedOriginal(string name,string hash,Action<Action> originalSession,CancellationToken token)
        => ReadArchivedOriginals<FrozenInput?>(originalSession,token,null,c =>
        {
            var bytes = c.ExecuteScalar<byte[]>("SELECT raw FROM performance_documents WHERE hash=@hash AND length(raw) BETWEEN 1 AND 26214400",
                new { hash });
            return bytes is null ? null : FrozenInput.FromBytes(name,bytes);
        });

    private T ReadArchivedOriginals<T>(Action<Action> originalSession,CancellationToken token,T empty,Func<SqliteConnection,T> read)
    {
        var result = empty;
        originalSession(() =>
        {
            token.ThrowIfCancellationRequested();
            var active = Required();
            using var lease = active.Enter();
            using var c = active.Open();
            if (c.ExecuteScalar<int>("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='performance_documents')") != 0)
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
                var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this),active.Lifetime);
                document=new PerformanceFiles(CaptureGuard(),exports).Read(input,broker,cancellation,sourceFacts);
                cancellation.ThrowIfCancellationRequested();
            });
        }
        return document!;
    }
    public void CommitCalculation(CalculationResult result,IReadOnlyList<PerformanceDocument> documents,Action<Action> originalSession,CancellationToken token,bool allowDuplicate)
    {
        // 两个模块共用同一连接和事务；任一保存失败或提交前取消，双方业务写入同时回滚。
        lock(gate)originalSession(()=>
        {
            var active=Required();token.ThrowIfCancellationRequested();
            var performance=new PerformanceStore(active);
            using var lease=active.Enter();using var c=active.Open();using var tx=c.BeginTransaction();
            foreach(var document in documents){token.ThrowIfCancellationRequested();performance.Import(c,tx,document);}
            repository!.Save(c,tx,result,allowDuplicate);
            token.ThrowIfCancellationRequested();active.Lifetime.ThrowIfCancellationRequested();tx.Commit();
        });
    }
    /// <summary>绑定真实会话；online 决定是否提供行情适配器，绩效页面启用在线访问，锁定后取消。</summary>
    public IPerformanceUseCases CreatePerformanceUseCases(bool online=true)
    {
        lock(gate)
        {
            var active=Required();var exports=new CRS.Application.SensitiveDataExportService(new DesktopOperations(repository!,"",this),active.Lifetime);
            return new PerformanceUseCases(new PerformanceStore(active),new PerformanceFiles(CaptureGuard(),exports),active.Lifetime,
                online ? new YahooPerformanceMarketData() : null, online ? new OfficialAnnualExchangeRates().ReadCachedValuationRates : null);
        }
    }
}
