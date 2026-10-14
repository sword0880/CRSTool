using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Domain.Performance;

namespace CRS.Infrastructure;

/// <summary>固定格式与有界解码，金额用 JSON Decimal 保留精度；输入摘要与历史结果都独立于税务数据。</summary>
internal static class PerformanceCodec
{
    internal const int Maximum=64*1024*1024;
    internal static readonly JsonSerializerOptions Options=new()
    {
        MaxDepth=32,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true,
        Converters={new JsonStringEnumConverter(allowIntegerValues:false)}
    };
    internal static byte[] Encode<T>(T value)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Options);
        if(bytes.Length>Maximum)throw new CrsException("绩效资料超过 64 MiB 单份快照限额。");return bytes;
    }
    internal static T Decode<T>(byte[] bytes)
    {
        if(bytes.Length is <=0 or >Maximum)throw new CrsException("绩效资料为空或超过读取限额。");
        try
        {
            using var doc=JsonDocument.Parse(bytes,new(){MaxDepth=32}); Check(doc.RootElement);
            return JsonSerializer.Deserialize<T>(bytes,Options)??throw new CrsException("绩效资料为空。");
        }
        catch(JsonException) {throw new CrsException("绩效 JSON 字段、枚举、重复属性或格式无效，请按模板核对。");}
        static void Check(JsonElement e)
        {
            if(e.ValueKind==JsonValueKind.Object)
            {var names=new HashSet<string>();foreach(var p in e.EnumerateObject()){if(!names.Add(p.Name))throw new JsonException();Check(p.Value);}}
            // 集合不允许空项；金额等可空字段仍可用 null 表达资料缺失。
            else if(e.ValueKind==JsonValueKind.Array)foreach(var item in e.EnumerateArray()){if(item.ValueKind==JsonValueKind.Null)throw new JsonException();Check(item);}
            else if(e.ValueKind==JsonValueKind.String && e.GetString()!.Length>4096)throw new JsonException();
        }
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static PerformanceData Ordered(PerformanceData d)=>d with
    {Accounts=d.Accounts.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Events=d.Events.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),
     Valuations=d.Valuations.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Prices=d.Prices.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),
     Fx=d.Fx.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Coverage=d.Coverage.OrderBy(x=>x.AccountId,StringComparer.Ordinal).ThenBy(x=>x.From).ThenBy(x=>x.Source.DocumentId,StringComparer.Ordinal).ToArray(),
     Transfers=d.Transfers.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray(),Revisions=d.Revisions.OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id,StringComparer.Ordinal).ToArray()};
}
