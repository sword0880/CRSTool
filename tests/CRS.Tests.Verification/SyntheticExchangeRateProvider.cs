using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application.Abstractions;
using CRS.Domain;

/// <summary>仅供离线验证和性能基准使用的合成汇率，不依赖用户 JSON 配置。</summary>
internal sealed class SyntheticExchangeRateProvider : IExchangeRateProvider
{
    private readonly AppliedRate[] rates =
    [
        new(2025, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
        new(2025, "HKD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), false, true),
        new(2025, "USD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), false, true),
        new(2026, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
        new(2026, "HKD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), false, true),
        new(2026, "USD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), false, true)
    ];

    public string Fingerprint { get; } =
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new AppliedRate(2025, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
            new AppliedRate(2025, "HKD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率", new(2025, 1, 1), new(2025, 12, 31), false, true),
            new AppliedRate(2025, "USD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率", new(2025, 1, 1), new(2025, 12, 31), false, true),
            new AppliedRate(2026, "CNY", 1m, "人民币本币", "", "本币折算", null, null, false, true),
            new AppliedRate(2026, "HKD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率", new(2026, 1, 1), new(2026, 12, 31), false, true),
            new AppliedRate(2026, "USD", 1m, "合成验证数据", "https://example.com/verification", "固定测试汇率", new(2026, 1, 1), new(2026, 12, 31), false, true)
        })));

    public List<AppliedRate> ForYear(int year) => rates.Where(rate => rate.Year == year).OrderBy(rate => rate.Currency).ToList();

    public AppliedRate Get(int year, string currency) => rates.FirstOrDefault(rate =>
        rate.Year == year && rate.Currency == currency) ?? throw new CrsException($"缺少 {year} 年 {currency} 合成测试汇率。");
}
