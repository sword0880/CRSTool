using CRS.Common.Integrity;
using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>按知识时点读取的单市场原件事实；股票市场整体成交，不是指数样本或具名机构成交。</summary>
public sealed record MarketActivityKnowledgeRevision(string Id,string Market,DateOnly Date,decimal AmountYuan,decimal VolumeShares,
    string Decision,MarketCycleObservationState State,DateTimeOffset FirstSeenAt,DateTimeOffset StateRecordedAt,
    DateTimeOffset SourceRetrievedAt,string KnowledgeOrigin,string EvidenceHash,string OriginalHash,string SourceUrl,string RegistrationHash,string StateEventHash,bool EvidenceHealthy,string? StateCommandId=null)
{
    public DateTimeOffset? SourcePublishedAt=>null;
    public string StateText=>State switch{MarketCycleObservationState.Accepted=>"可用于研究",MarketCycleObservationState.NeedsReview=>"差异待核验",MarketCycleObservationState.Critical=>"原件或登记异常",_=>"已否决"};
    public string KnowledgeText=>KnowledgeOrigin=="Native"?"已记录实际登记时刻":"旧版资料，本次迁移起可知";
}

public sealed record MarketActivityKnowledgeReport(DateOnly From,DateOnly Through,DateTimeOffset Cutoff,
    IReadOnlyList<MarketActivityKnowledgeRevision> Revisions,string Fingerprint)
{
    public const string Version="market-activity-knowledge-v1";
    public string FormatVersion {get;init;}=Version;
    public string Scope=>"SSEAndSZSEStockMarketActivity";
    public string TimingProof=>"SystemRegistrationAndReviewOnly";
    public string InstitutionTradingEvidence=>"NotObserved";
    public static MarketActivityKnowledgeReport Freeze(DateOnly from,DateOnly through,DateTimeOffset cutoff,IReadOnlyList<MarketActivityKnowledgeRevision> revisions)
    {
        var ordered=revisions.OrderBy(x=>x.Date).ThenBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.FirstSeenAt).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray();
        var canonical=JsonSerializer.SerializeToUtf8Bytes(new{Version,From=from,Through=through,Cutoff=cutoff.ToUniversalTime(),
            Revisions=ordered.Select(x=>new{x.Id,x.Market,x.Date,x.AmountYuan,x.VolumeShares,x.Decision,x.State,
                FirstSeenAt=x.FirstSeenAt.ToUniversalTime(),StateRecordedAt=x.StateRecordedAt.ToUniversalTime(),SourceRetrievedAt=x.SourceRetrievedAt.ToUniversalTime(),
                x.KnowledgeOrigin,x.EvidenceHash,x.OriginalHash,x.SourceUrl,x.RegistrationHash,x.StateEventHash,x.EvidenceHealthy,x.StateCommandId}).ToArray()});
        var displayed=ordered.Select(x=>x with{FirstSeenAt=CRS.Timing.BeijingClock.Normalize(x.FirstSeenAt),StateRecordedAt=CRS.Timing.BeijingClock.Normalize(x.StateRecordedAt),SourceRetrievedAt=CRS.Timing.BeijingClock.Normalize(x.SourceRetrievedAt)}).ToArray();
        return new(from,through,CRS.Timing.BeijingClock.Normalize(cutoff),displayed,ContentFingerprint.Sha256(canonical));
    }
}
