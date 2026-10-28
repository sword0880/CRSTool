using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class IndexSeriesStore
{
    public (IReadOnlyList<MarketCycleIndexPoint> Points,IReadOnlyList<MarketCycleSourceReference> Sources) CyclePrices(DateOnly from,DateOnly through,DateTimeOffset cutoff)
    {
        var revisions=IndexSeriesRevisions(from,through,cutoff).Where(x=>x.Value.Kind=="Price"&&x.Value.Code=="399317"&&x.State is not ("Rejected" or "Superseded")).ToArray();
        var points=new List<MarketCycleIndexPoint>();var sources=new List<MarketCycleSourceReference>();
        foreach(var row in revisions)
        {
            _session.OperationCancellation.ThrowIfCancellationRequested();
            var json=_session.Scalar("SELECT proof_json FROM nt_index_capture WHERE id=$id",("$id",row.CaptureId)) as string??throw new InvalidDataException("价格捕获证明缺失。");
            var proof=JsonSerializer.Deserialize<IndexCaptureProof>(json)??throw new InvalidDataException("价格捕获证明损坏。");
            var data=proof.Documents.SingleOrDefault(x=>x.Role=="Price")??throw new InvalidDataException("指数价格原件角色缺失。");
            var state=!row.EvidenceHealthy?MarketCycleObservationState.Critical:row.State=="Accepted"?MarketCycleObservationState.Accepted:MarketCycleObservationState.NeedsReview;
            // 输入首次可用时间取此状态实际登记时刻；原修订首次发现时刻仍在Sources中独立保留。
            points.Add(new(row.Value.Date,row.Value.Close,null,row.StateRecordedAt,proof.Documents.Max(x=>x.RetrievedAt),row.Id,data.Hash,state));
            foreach(var d in proof.Documents)sources.Add(new("Price",row.Value.Date,row.Id,d.Role,d.Url,d.Hash,d.Hash,d.RetrievedAt,row.FirstSeenAt,row.StateRecordedAt,state.ToString()));
        }
        return(points,sources);
    }
}
