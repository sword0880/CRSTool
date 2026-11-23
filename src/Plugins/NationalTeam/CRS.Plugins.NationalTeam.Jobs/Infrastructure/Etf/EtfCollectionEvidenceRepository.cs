using CRS.Common.Concurrency;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

public sealed partial class MarketRepository:IEtfCollectionEvidenceRepository
{
    private sealed class CollectionEvidenceVersion(object owner,EtfKey[] pool,DateTimeOffset captured):IEtfCollectionEvidenceVersion
    {
        public object Owner{get;}=owner;
        public EtfKey[] Pool{get;}=pool;
        public DateTimeOffset Captured{get;}=captured;
        public int Consumed;
    }
    private EtfKey[] CollectionPool()=>etfStore.Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).OrderBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Ticker,StringComparer.Ordinal).ToArray();
    public IEtfCollectionEvidenceVersion CaptureCollectionEvidenceVersion()
    {
        if(!database.IsReadSnapshot)throw new InvalidOperationException("采集池版本必须来自独立只读快照。");
        return new CollectionEvidenceVersion(database.EvaluationOwner,CollectionPool(),clock.GetUtcNow());
    }
    public void ValidateCollectionEvidence()
    {
        if(!database.IsReadSnapshot)throw new InvalidOperationException("份额批次预读必须使用独立只读快照。");
        database.ValidateEvidence(dashboardOnly:true);
    }
    public void RequireCollectionEvidenceVersion(IEtfCollectionEvidenceVersion version)
    {
        if(!ReferenceEquals(database,database.EvaluationOwner))throw new InvalidOperationException("采集池版本只能在写会话内核对。");
        if(version is not CollectionEvidenceVersion issued||!ReferenceEquals(issued.Owner,database.EvaluationOwner)||raw.StableEvidence is not{IsActive:true})
            throw new InvalidDataException("份额批次版本不属于当前资料库，或原件稳定租约已结束。");
        if(Interlocked.Exchange(ref issued.Consumed,1)!=0)throw new InvalidOperationException("份额批次版本只能提交一次。");
        var now=clock.GetUtcNow();
        // 这里只准备原件和按池筛选的字节，没有准备金融结论；其它事实在原提交体重新读取并核验。
        if(now<issued.Captured||now.ToOffset(TimeSpan.FromHours(8)).Date!=issued.Captured.ToOffset(TimeSpan.FromHours(8)).Date||!CollectionPool().SequenceEqual(issued.Pool))
            throw new WorkAdmissionException("InputsChanged");
    }
}
