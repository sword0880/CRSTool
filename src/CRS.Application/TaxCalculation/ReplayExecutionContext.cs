using System.Text.Json;

namespace CRS.Application;

/// <summary>历史复算仅使用已记录的汇率和执行口径，缺失时拒绝回退到当前配置。</summary>
internal sealed record ReplayExecutionContext(bool Provisional, string Configuration,
    IReadOnlyDictionary<(int Year, string Currency), AppliedRate> Rates)
{
    internal const string SupportedAlgorithm = "FIFO_TOTAL_COST_V2";

    public static ReplayExecutionContext Read(CalculationResult snapshot)
    {
        try
        {
            using var audit = JsonDocument.Parse(snapshot.SnapshotJson);
            var root = audit.RootElement;
            if (root.GetProperty("year").GetInt32() != snapshot.Year
                || root.GetProperty("policy").GetString() != TaxEngine.PolicyVersion
                || root.GetProperty("algorithm").GetString() != SupportedAlgorithm)
                throw new CrsException("历史年度、税务规则或 FIFO 算法与当前复算口径不兼容，不能重放。");
            var configuration = root.GetProperty("configuration").GetString();
            if (string.IsNullOrWhiteSpace(configuration))
                throw new CrsException("历史记录缺少汇率配置指纹，不能重放。");
            var frozen = new Dictionary<(int, string), AppliedRate>();
            foreach (var rate in snapshot.Rates)
            {
                if (rate.Year != snapshot.Year || string.IsNullOrWhiteSpace(rate.Currency) || rate.Value <= 0
                    || !frozen.TryAdd((rate.Year, rate.Currency), rate))
                    throw new CrsException("历史冻结汇率存在错年度、无效值或重复币种，不能重放。");
            }
            return new(snapshot.Provisional, configuration, frozen);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CrsException("历史执行元数据缺失或格式无效，不能重放；仍可查看冻结结果或重新导入原件。");
        }
    }

    public AppliedRate GetRate(int year, string currency) => Rates.TryGetValue((year, currency), out var rate)
        ? rate : throw new CrsException($"历史冻结汇率缺少 {year} 年 {currency}，不能使用当前配置代替。");
}
