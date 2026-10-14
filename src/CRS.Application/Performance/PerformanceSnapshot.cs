using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Domain.Performance;

namespace CRS.Application.Performance;

/// <summary>内容身份覆盖全部计算事实与修订；沿用既有快照排序及枚举 JSON 表示。</summary>
public static class PerformanceSnapshot
{
    private static readonly JsonSerializerOptions Json=new(){Converters={new JsonStringEnumConverter(allowIntegerValues:false)}};
    public static PerformanceData Ordered(PerformanceData d)=>d with
    {
        Accounts=d.Accounts.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Events=d.Events.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),
        Valuations=d.Valuations.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Prices=d.Prices.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),
        Fx=d.Fx.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Coverage=d.Coverage.OrderBy(x=>x.AccountId,StringComparer.Ordinal).ThenBy(x=>x.From).ThenBy(x=>x.Source.DocumentId,StringComparer.Ordinal).ToArray(),
        Transfers=d.Transfers.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Revisions=d.Revisions.OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray()
    };
    public static string Identity(PerformanceData data)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(Ordered(data),Json);
        if(bytes.Length>64*1024*1024)throw new CrsException("绩效资料超过 64 MiB 单份快照限额。");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
