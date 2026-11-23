using System.Globalization;
using System.Text;
using CRS.Common.Concurrency;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class EtfUseCases
{
    private sealed record PreparedCollectionPage(SourcePage Source,IReadOnlyList<ImportRow> Rows,string OriginalHash,string NormalizedHash);
    private sealed record CollectionCommitResult(ShareImportOutcome Outcome,long PreparationMilliseconds,long CommitHeldMilliseconds,int Attempts);
    /// <summary>预读及原件落盘不占写名额；原规则仍在提交体中读取当前库并核验全部依赖。</summary>
    private async Task<CollectionCommitResult> CommitCollectedDayAsync(IReadOnlyList<SourcePage> pages,IReadOnlyList<EtfKey> poolKeys,DateOnly day,
        Func<IEtfRepository,IReadOnlyList<PreparedCollectionPage>,ShareImportOutcome> commit,CancellationToken token)
    {
        if(raw is not IRawEvidenceStability stability||raw is not IRawEvidenceVerifier verifier)throw new NotSupportedException("份额提交需要有界原件稳定租约及核验能力。");
        long preparation=0,held=0;
        for(var attempt=1;;attempt++)
        {
            token.ThrowIfCancellationRequested();using var stable=stability.HoldVerifiedEvidence();
            var preparing=System.Diagnostics.Stopwatch.GetTimestamp();
            var snapshot=await ReadPortAsync((IEtfRepository r)=>
            {
                ((IEtfCollectionEvidenceRepository)r).ValidateCollectionEvidence();
                return(Version:((IEtfCollectionEvidenceRepository)r).CaptureCollectionEvidenceVersion(),Pool:r.Configuration().Where(x=>x.Active).Select(x=>new EtfKey(x.Market,x.Ticker)).ToHashSet());
            },token).ConfigureAwait(false);
            var prepared=await Task.Run(()=>
            {
                var result=new List<PreparedCollectionPage>();
                foreach(var page in pages)
                {
                    token.ThrowIfCancellationRequested();var rows=page.Rows.Where(x=>snapshot.Pool.Contains(x.Key)&&poolKeys.Contains(x.Key)&&x.Date==day).ToArray();
                    var normalized=Encoding.UTF8.GetBytes("market,ticker,trade_date,total_shares,unit\n"+string.Join('\n',rows.Select(x=>$"{x.Key.Market},{x.Key.Ticker},{x.Date:yyyy-MM-dd},{x.Original.ToString(CultureInfo.InvariantCulture)},万份")));
                    result.Add(new(page,rows,raw.Save(page.Content,token),raw.Save(normalized,token)));
                }
                var verified=verifier.VerifyMany(result.SelectMany(x=>new[]{x.OriginalHash,x.NormalizedHash}),token);
                if(verified.Values.Any(x=>!x))throw new InvalidDataException("份额批次原件损坏，拒绝提交。");
                return result;
            },token).ConfigureAwait(false);
            preparation+=(long)System.Diagnostics.Stopwatch.GetElapsedTime(preparing).TotalMilliseconds;
            try
            {
                var outcome=await RunMeasuredAsync((IEtfRepository r)=>
                {((IEtfCollectionEvidenceRepository)r).RequireCollectionEvidenceVersion(snapshot.Version);return commit(r,prepared);},token,value=>held+=value).ConfigureAwait(false);
                return new(outcome,preparation,held,attempt);
            }
            catch(WorkAdmissionException error) when(error.Reason=="InputsChanged"&&attempt<3){}
        }
    }
}
