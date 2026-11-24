using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Domain;

/// <summary>仅供自动化测试使用的固定汇率，不读取正式用户配置或 JSON 文件。</summary>
internal sealed class SyntheticExchangeRateProvider : IExchangeRateProvider
{
    private readonly AppliedRate[] rates =
    [
        new(2025, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
        new(2025, "USD", 1m, "来源未登记", "", "", null, null, false, false)
    ];

    public string Fingerprint { get; } =
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new AppliedRate(2025, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
            new AppliedRate(2025, "USD", 1m, "来源未登记", "", "", null, null, false, false)
        })));

    public List<AppliedRate> ForYear(int year) => rates.Where(rate => rate.Year == year).OrderBy(rate => rate.Currency).ToList();

    public AppliedRate Get(int year, string currency) => rates.FirstOrDefault(rate =>
        rate.Year == year && rate.Currency == currency) ?? throw new CrsException($"缺少 {year} 年 {currency} 合成测试汇率。");
}
