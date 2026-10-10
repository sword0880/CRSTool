using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRS.Infrastructure;

/// <summary>读取用户明确选择的抵免明细与凭证，冻结文件摘要，正文和路径不进入结果。</summary>
public static class ForeignCreditFiles
{
    public static ForeignCreditEvidence Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is <= 0 or > 1024 * 1024) throw new CrsException("抵免明细必须非空且不能超过 1 MB。");
        // 必填构造参数不允许被静默填成零或空值，拼错字段同样拒绝。
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        InputDocument document;
        try { document = JsonSerializer.Deserialize<InputDocument>(input, options) ?? throw new JsonException(); }
        catch (JsonException) { throw new CrsException("抵免明细 JSON 格式无效，请参照模板填写。"); }
        if (document.Income is null || document.Payments is null || document.Income.Count > 10000 || document.Payments.Count > 10000)
            throw new CrsException("抵免明细集合缺失或记录过多。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var payments = document.Payments.Select(t =>
        {
            if (t is null || t.Proofs is null || t.Proofs.Count is < 1 or > 10) throw new CrsException("每笔税款需提供 1 至 10 份凭证。");
            var proofs = t.Proofs.Select(p =>
            {
                if (p is null || string.IsNullOrWhiteSpace(p.Path)) throw new CrsException("凭证路径不能为空。");
                var proofPath = Path.GetFullPath(p.Path, directory);
                using var stream = File.OpenRead(proofPath);
                // 大小校验和摘要使用同一个句柄，避免两次读取取得不同凭证。
                if (stream.Length is <= 0 or > 25 * 1024 * 1024) throw new CrsException("单份凭证必须非空且不能超过 25 MB。");
                return new CreditProof(p.Role, Path.GetFileName(proofPath), Convert.ToHexString(SHA256.HashData(stream)));
            }).ToList();
            return new CreditPayment(t.Broker, t.Account, t.Country, t.Category, t.Currency, t.Amount,
                t.Authority, t.Reference, t.Exclusion, proofs);
        }).ToList();
        return new(document.Version, document.Year, document.Income, payments);
    }

    private sealed record InputDocument(int Version, int Year, List<CreditIncome> Income, List<InputPayment> Payments);
    private sealed record InputProof(string Role, string Path);
    private sealed record InputPayment(string Broker, string Account, string Country, string Category, string Currency,
        decimal Amount, string Authority, string Reference, string Exclusion, List<InputProof> Proofs);
}
