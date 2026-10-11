using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CRS.Application;

/// <summary>用途明确的确定性编码：类型标签、长度前缀、排序键和无指数十进制，避免依赖 JSON 属性顺序。</summary>
public static class CanonicalEvidence
{
    public const string Version="CRS.EvidenceCanonical.v1";
    public static string Hash(JsonElement value)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string text)=>hash.AppendData(Encoding.UTF8.GetBytes(text));
        void String(string text) {var bytes=Encoding.UTF8.GetBytes(text); Text("s"+bytes.Length.ToString(CultureInfo.InvariantCulture)+":"); hash.AppendData(bytes);}
        void Write(JsonElement item)
        {
            switch(item.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties=item.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray();
                    if(properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=properties.Length) throw new CrsException("证据字段重复。");
                    Text("o"+properties.Length.ToString(CultureInfo.InvariantCulture)+":");
                    foreach(var p in properties) {String(p.Name); Write(p.Value);} break;
                case JsonValueKind.Array:
                    Text("a"+item.GetArrayLength().ToString(CultureInfo.InvariantCulture)+":"); foreach(var i in item.EnumerateArray()) Write(i); break;
                case JsonValueKind.String: String(item.GetString()!); break;
                case JsonValueKind.Number:
                    if(!item.TryGetDecimal(out var number)) throw new CrsException("证据数字超出十进制范围。");
                    var numeric=number==0?"0":number.ToString("0.############################",CultureInfo.InvariantCulture);
                    Text("d"+numeric.Length.ToString(CultureInfo.InvariantCulture)+":"+numeric); break;
                case JsonValueKind.True: Text("t"); break;
                case JsonValueKind.False: Text("f"); break;
                case JsonValueKind.Null: Text("n"); break;
                default: throw new CrsException("证据格式无效。");
            }
        }
        Text(Version+"|"); Write(value); return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static string PayloadHash(CalculationResult result)
    {
        var node=JsonSerializer.SerializeToNode(result)!.AsObject(); node.Remove(nameof(CalculationResult.EvidenceSeal));
        return Hash(JsonSerializer.SerializeToElement(node));
    }
}

/// <summary>封存整份持久结果与执行摘要；这是完整性证据，不是数字签名或来源真实性认证。</summary>
public sealed record RunEvidenceSeal(int Version,string Encoding,string ApplicationSha256,string DomainSha256,
    string InfrastructureSha256,string PayloadDigest,string EvidenceDigest)
{
    public static RunEvidenceSeal Create(CalculationResult result,string infrastructureSha256)
    {
        var seal=new RunEvidenceSeal(1,CanonicalEvidence.Version,AssemblyHash(typeof(CalculationResult).Assembly.Location),
            AssemblyHash(typeof(FifoEngine).Assembly.Location),infrastructureSha256,CanonicalEvidence.PayloadHash(result),"");
        return seal with { EvidenceDigest=HeaderHash(seal) };
    }
    // 执行版本也纳入摘要，防止只改程序集指纹而绕过业务载荷校验。
    private static string HeaderHash(RunEvidenceSeal seal)=>CanonicalEvidence.Hash(JsonSerializer.SerializeToElement(new
        {seal.Version,seal.Encoding,seal.ApplicationSha256,seal.DomainSha256,seal.InfrastructureSha256,seal.PayloadDigest}));
    public static void Verify(CalculationResult result)
    {
        if(result.EvidenceSeal is not { } seal) return; // 明确的旧记录仍可查看，不冒充已有执行封存。
        if(seal.Version!=1 || seal.Encoding!=CanonicalEvidence.Version || seal.PayloadDigest!=CanonicalEvidence.PayloadHash(result)
            || seal.EvidenceDigest!=HeaderHash(seal))
            throw new CrsException("运行证据封存摘要不一致，结果可能已改变，不能继续使用。");
    }
    public static string AssemblyHash(string path) {using var stream=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream));}
}
