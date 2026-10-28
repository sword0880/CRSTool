using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>批量读取使用显式能力版本；未知服务不能把全池请求解释为逐证券响应。</summary>
public sealed record EtfQueryCapabilities(int Version,IReadOnlyList<string> Capabilities,int MaximumPageCount)
{
    public const int CurrentVersion=1;
    public const string BatchAcceptance="etf-acceptance-batch-v1";
    public const string AsyncResearch="research-snapshot-jobs-v1";
    public IReadOnlyList<string>? ShortReadOperations {get;init;}
}
/// <summary>作业受理和结果就绪分开；失败、取消、过期均不返回金融行。</summary>
public sealed record EtfResearchTicket(string Id,string State,string? Snapshot,string? Failure,DateTimeOffset ExpiresAt,string RequestFingerprint,int Version=1);
public sealed record EtfAcceptanceBatchMetadata(int Version,DateOnly Through,int ExpectedEtfs,string PoolFingerprint)
{
    public static string Fingerprint(IEnumerable<EtfAcceptance> rows)=>ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(
        rows.Select(x=>new EtfKey(x.Market,x.Ticker)).OrderBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Ticker,StringComparer.Ordinal).ToArray()));
}
