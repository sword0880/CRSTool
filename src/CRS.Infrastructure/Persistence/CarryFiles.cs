using System.Text.Json;
using CRS.Domain;

namespace CRS.Infrastructure;

public static class CarryFiles
{
    private sealed record Envelope(string Format, string Sha256, CarryDocument Data);
    /// <summary>仅从可信年末库存创建结转，保存成本、费用及舍入余差。</summary>
    public static CarryDocument Create(CalculationResult r)
    {
        if (!r.CarryEligible) throw new CrsException("成本或持仓链未完成核对，不能生成可信结转。");
        return new(1, r.Year, r.SnapshotId, r.Sources.Where(s => !s.Opening && s.Start.Year <= r.Year && s.End.Year >= r.Year).Select(s => s.Account).Distinct().Order().ToArray(), r.EndingLots, r.EndingRounding);
    }
    /// <summary>序列化带版本及 SHA256 的 C# 版结转格式。</summary>
    public static byte[] Encode(CarryDocument document) => JsonSerializer.SerializeToUtf8Bytes(new Envelope("CRS_DOTNET_LOT_CARRY", XmlFields.HashJson(document), document));
    /// <summary>限量读取结转文件，验证格式、摘要及内部批次约束。</summary>
    public static CarryDocument Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new CrsException("结转文件不能超过 2 MB。");
        try
        {
            var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path));
            if (e is null || e.Format != "CRS_DOTNET_LOT_CARRY" || e.Data is null || e.Sha256 != XmlFields.HashJson(e.Data)) throw new CrsException("结转格式或摘要无效。");
            Validate(e.Data, e.Data.Year + 1, e.Data.Accounts); return e.Data;
        }
        catch (JsonException) { throw new CrsException("结转文件不是有效的 C# 版 JSON。"); }
    }
    /// <summary>验证结转对应上一年度及相同账户，批次唯一且成本与舍入余额有效。</summary>
    public static void Validate(CarryDocument d, int year, string[] accounts)
    {
        if (d.Version != 1 || d.Year != year - 1 || d.Year is < 2000 or > 2100 || string.IsNullOrWhiteSpace(d.SourceSnapshotId)
            || d.Accounts is null || d.Lots is null || d.Rounding is null || d.Accounts.Length == 0
            || !d.Accounts.Order().SequenceEqual(accounts.Order()) || d.Accounts.Distinct().Count() != d.Accounts.Length)
            throw new CrsException("结转年度、账户或版本不匹配。");
        var ids = new HashSet<(SecurityKey, string)>();
        foreach (var l in d.Lots)
            if (l.Quantity <= 0 || l.Cost < 0 || l.Fee < 0 || l.BuyTime.Year > d.Year || !d.Accounts.Contains(l.Key.Account)
                || string.IsNullOrWhiteSpace(l.Key.Instrument) || string.IsNullOrWhiteSpace(l.Key.Currency) || string.IsNullOrWhiteSpace(l.RecordId)
                || !ids.Add((l.Key, l.RecordId))) throw new CrsException("结转批次重复、成本无效或日期越界。");
        var keys = new HashSet<SecurityKey>();
        foreach (var r in d.Rounding)
            if (!d.Accounts.Contains(r.Key.Account) || !keys.Add(r.Key) || Math.Abs(r.CostDelta) > .005m || Math.Abs(r.FeeDelta) > .005m)
                throw new CrsException("结转舍入余额重复或无效。");
    }
}
